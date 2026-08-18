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
        - transfer_budget: move budget headroom from one category to another within the same month. Prefer
          this over two separate set_budget calls whenever the user asks to reallocate/move budget between
          categories — it's atomic and gives them one thing to review instead of two. Call check_budget_status
          first so you propose a source category that's actually likely to have enough headroom. If it still
          reports insufficient funds, don't retry blindly or guess a smaller amount yourself — turn its
          response (which lists alternative categories with headroom, if any) into a clarifying question for
          the user: proceed with a suggested category, a smaller amount, or skip the transfer.
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

    /// <summary>
    /// Moves budget headroom from one category to another within the same month, atomically (one
    /// <see cref="FinanceDbContext.SaveChangesAsync(CancellationToken)"/> covers both rows — EF Core's
    /// default per-call transaction is what gives this the atomicity two separate <c>set_budget</c> calls
    /// would lack). Writes nothing at all if the source doesn't have enough headroom — instead of silently
    /// under-transferring or guessing, returns the shortfall plus up to 2 alternative categories that do
    /// have enough (deterministic, computed here rather than hoping the model calls <c>check_budget_status</c>
    /// first — see <see cref="Instructions"/> for the companion behavior that asks it to anyway).
    /// </summary>
    [AgentSkillScript("transfer_budget")]
    public async Task<string> TransferBudgetAsync(string fromCategoryName, string toCategoryName, decimal amount, string? periodMonth)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider);

        var fromCategory = await FindCategoryAsync(db, fromCategoryName);
        if (fromCategory is null)
        {
            return $"Error: no category named '{fromCategoryName}' found.";
        }

        var toCategory = await FindCategoryAsync(db, toCategoryName);
        if (toCategory is null)
        {
            return $"Error: no category named '{toCategoryName}' found.";
        }

        var period = NormalizeToMonthStart(ParseDateOrToday(periodMonth));

        var fromBudget = await db.Budgets.FirstOrDefaultAsync(b =>
            b.UserId == userId && b.CategoryId == fromCategory.Id && b.PeriodMonth == period);

        if (fromBudget is null || fromBudget.LimitAmount < amount)
        {
            var available = fromBudget?.LimitAmount ?? 0m;

            var alternatives = await db.Budgets
                .Include(b => b.Category)
                .Where(b => b.UserId == userId && b.PeriodMonth == period
                    && b.CategoryId != fromCategory.Id && b.LimitAmount >= amount)
                .OrderByDescending(b => b.LimitAmount)
                .Take(2)
                .ToListAsync();

            var suggestion = alternatives.Count > 0
                ? " Categories with enough headroom instead: " +
                  string.Join(", ", alternatives.Select(b => $"{b.Category.Name} ({b.LimitAmount:C})")) + "."
                : "";

            return $"Cannot transfer {amount:C} from {fromCategory.Name} for {period:yyyy-MM} — only " +
                   $"{available:C} available.{suggestion} Ask the user how they'd like to proceed.";
        }

        var toBudget = await db.Budgets.FirstOrDefaultAsync(b =>
            b.UserId == userId && b.CategoryId == toCategory.Id && b.PeriodMonth == period);

        fromBudget.LimitAmount -= amount;
        if (toBudget is null)
        {
            toBudget = new Budget
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CategoryId = toCategory.Id,
                PeriodMonth = period,
                LimitAmount = amount,
            };
            db.Budgets.Add(toBudget);
        }
        else
        {
            toBudget.LimitAmount += amount;
        }

        await db.SaveChangesAsync();

        return $"Transferred {amount:C} from {fromCategory.Name} to {toCategory.Name} for {period:yyyy-MM}. " +
               $"{fromCategory.Name} is now {fromBudget.LimitAmount:C}, {toCategory.Name} is now {toBudget.LimitAmount:C}.";
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
