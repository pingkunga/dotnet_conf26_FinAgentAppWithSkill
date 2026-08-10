using System.Globalization;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Core.Repositories;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceApp.Skills.Budgeting;

/// <summary>
/// Class-based skill (docs/spec.md §4.1) for transactions and budgets. Constructed with the current
/// authenticated user's id captured at session-start (via <c>ChatSessionService</c>/
/// <c>ICurrentUserAccessor</c>, §3.4/§2a point 3) — every script below operates against that one user,
/// never a user id taken from an LLM-provided argument (§2a point 7).
/// </summary>
/// <remarks>
/// Scripts fire via <paramref name="scopeFactory"/>.CreateScope() — a background context with no HTTP
/// request/Blazor circuit. Resolving <see cref="FinanceDbContext"/> normally from that scope would go
/// through the real (ASP.NET-backed) <see cref="ICurrentUserAccessor"/>, which has nothing to derive a
/// user from outside a request and returns null — silently filtering every query to zero rows regardless
/// of <paramref name="userId"/>. So each script resolves only the scope's
/// <see cref="DbContextOptions{FinanceDbContext}"/> and constructs <see cref="FinanceDbContext"/> manually
/// with a <see cref="FixedCurrentUserAccessor"/> bound to <paramref name="userId"/> — the concrete
/// mechanism that makes "a skill has no other source of 'which user' than what it was given at
/// construction" true. The same pattern applies to <c>FinanceApp.McpServer</c> (§5).
/// </remarks>
public sealed class BudgetSkill(IServiceScopeFactory scopeFactory, Guid userId)
    : AgentClassSkill<BudgetSkill>(argumentMarshaler: null)
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        name: "budgeting",
        description: "Record transactions, set per-category monthly budgets, and check budget status.",
        compatibility: null);

    protected override string Instructions =>
        """
        Use this skill to manage the user's transactions and budgets.
        - add_transaction: record a new expense or income.
        - list_transactions: show recent transactions, optionally filtered by date range or category.
        - set_budget: set or update a monthly spending limit for a category.
        - check_budget_status: report spend-vs-limit per category for a month, flagging Near (>=80% used)
          and Over (>100% used) categories. Read the budgeting-policy resource for how to phrase advice
          about Near/Over categories.
        Dates are ISO 8601 (yyyy-MM-dd). Amounts are positive numbers; category names are matched
        case-insensitively against the user's own categories and the global default set.
        """;

    [AgentSkillResource("budgeting-policy")]
    public string BudgetingPolicy =>
        """
        Near-budget categories (80-100% of limit spent): gently note the category is approaching its
        limit; don't discourage the specific purchase being discussed.
        Over-budget categories (>100% of limit spent): flag clearly and suggest either reducing spend in
        that category for the rest of the month or revisiting the limit with set_budget if it's
        consistently unrealistic.
        Never mention another user's data, budgets, or transactions under any circumstance.
        """;

    private FinanceDbContext CreateDbContext(IServiceProvider scopedProvider)
    {
        var options = scopedProvider.GetRequiredService<DbContextOptions<FinanceDbContext>>();
        return new FinanceDbContext(options, new FixedCurrentUserAccessor(userId));
    }

    [AgentSkillScript("add_transaction")]
    public async Task<string> AddTransactionAsync(decimal amount, string categoryName, string? description, string? occurredOn)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider);

        var category = await FindCategoryAsync(db, categoryName);
        if (category is null)
        {
            return $"Error: no category named '{categoryName}' found.";
        }

        var date = ParseDateOrToday(occurredOn);

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CategoryId = category.Id,
            Amount = amount,
            OccurredOn = date,
            Description = description,
            Source = TransactionSource.Agent,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return $"Added {amount:C} to {category.Name} on {date:yyyy-MM-dd}.";
    }

    [AgentSkillScript("list_transactions")]
    public async Task<string> ListTransactionsAsync(string? fromDate, string? toDate, string? categoryName)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider);

        var query = db.Transactions.Include(t => t.Category).Where(t => t.UserId == userId);

        if (fromDate is not null && DateOnly.TryParse(fromDate, CultureInfo.InvariantCulture, out var from))
        {
            query = query.Where(t => t.OccurredOn >= from);
        }

        if (toDate is not null && DateOnly.TryParse(toDate, CultureInfo.InvariantCulture, out var to))
        {
            query = query.Where(t => t.OccurredOn <= to);
        }

        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            query = query.Where(t => t.Category != null && t.Category.Name.ToLower() == categoryName.ToLower());
        }

        var transactions = await query.OrderByDescending(t => t.OccurredOn).Take(50).ToListAsync();

        if (transactions.Count == 0)
        {
            return "No transactions found for that filter.";
        }

        var lines = transactions.Select(t =>
            $"{t.OccurredOn:yyyy-MM-dd} | {t.Category?.Name ?? "(uncategorized)"} | {t.Amount:C}{(t.Description is null ? "" : $" | {t.Description}")}");

        return string.Join('\n', lines);
    }

    [AgentSkillScript("set_budget")]
    public async Task<string> SetBudgetAsync(string categoryName, decimal limitAmount, string? periodMonth)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider);

        var category = await FindCategoryAsync(db, categoryName);
        if (category is null)
        {
            return $"Error: no category named '{categoryName}' found.";
        }

        var period = NormalizeToMonthStart(ParseDateOrToday(periodMonth));

        var budget = await db.Budgets.FirstOrDefaultAsync(b =>
            b.UserId == userId && b.CategoryId == category.Id && b.PeriodMonth == period);

        if (budget is null)
        {
            budget = new Budget
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CategoryId = category.Id,
                PeriodMonth = period,
                LimitAmount = limitAmount,
            };
            db.Budgets.Add(budget);
        }
        else
        {
            budget.LimitAmount = limitAmount;
        }

        await db.SaveChangesAsync();

        return $"Set {category.Name} budget for {period:yyyy-MM} to {limitAmount:C}.";
    }

    [AgentSkillScript("check_budget_status")]
    public async Task<string> CheckBudgetStatusAsync(string? periodMonth)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider);

        var period = NormalizeToMonthStart(ParseDateOrToday(periodMonth));
        var statuses = await BudgetRepository.GetBudgetStatusAsync(db, userId, period);

        if (statuses.Count == 0)
        {
            return $"No budgets set for {period:yyyy-MM}.";
        }

        var lines = statuses.Select(s =>
        {
            var flag = s.IsOver ? " [OVER]" : s.IsNear ? " [NEAR]" : "";
            return $"{s.CategoryName}: {s.SpentAmount:C} / {s.LimitAmount:C} ({s.PercentUsed:F0}%){flag}";
        });

        return string.Join('\n', lines);
    }

    private async Task<Category?> FindCategoryAsync(FinanceDbContext db, string categoryName) =>
        await db.Categories.FirstOrDefaultAsync(c => c.Name.ToLower() == categoryName.ToLower());

    private static DateOnly ParseDateOrToday(string? isoDate) =>
        isoDate is not null && DateOnly.TryParse(isoDate, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.UtcNow);

    private static DateOnly NormalizeToMonthStart(DateOnly date) => new(date.Year, date.Month, 1);
}
