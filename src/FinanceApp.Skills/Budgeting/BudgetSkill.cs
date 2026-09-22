using System.Globalization;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Core.Formatting;
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
        description: "Record transactions, top up funds, set per-category monthly budgets, check budget " +
                      "status, and contribute to savings goals.",
        compatibility: null);

    protected override string Instructions =>
        """
        Use this skill to manage the user's transactions and budgets.
        - add_transaction: record a new expense or income against any category.
        - top_up_funds: record income specifically — use this instead of add_transaction whenever the user
          says they're "topping up", adding funds, or received income, since it always records against the
          Income category correctly without you having to guess a category name.
        - list_transactions: show recent transactions, optionally filtered by date range or category.
        - set_budget: set or update a monthly spending limit for a category. If the response notes the
          month's total allocation now exceeds income, mention that to the user as a heads-up, not a reason
          to refuse the change.
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
        - contribute_to_goal: record money put toward a savings goal by name — this records a real
          transaction and updates the goal's progress atomically. Prefer this over asking the user to edit
          the goal's "current amount" by hand.
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

    private async Task<string> GetPreferredCurrencyAsync(FinanceDbContext db)
    {
        var user = await db.Users.FindAsync(userId);
        return user?.PreferredCurrency ?? "USD";
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
        var currency = await GetPreferredCurrencyAsync(db);

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CategoryId = category.Id,
            Amount = amount,
            Currency = currency,
            OccurredOn = date,
            Description = description,
            Source = TransactionSource.Agent,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return $"Added {CurrencyFormatter.Format(amount, currency)} to {category.Name} on {date:yyyy-MM-dd}.";
    }

    /// <summary>
    /// Records income specifically — deliberately does not take a <c>categoryName</c> parameter, always
    /// resolving the Income category server-side. Taking a model-supplied category name here would reopen
    /// the exact misrouting risk this Income-lock exists to close.
    /// </summary>
    [AgentSkillScript("top_up_funds")]
    public async Task<string> TopUpFundsAsync(decimal amount, string? description, string? occurredOn)
    {
        if (amount <= 0)
        {
            return "Error: top-up amount must be greater than zero.";
        }

        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider);

        var incomeCategory = await db.Categories.FirstOrDefaultAsync(c => c.Id == SeedData.IncomeCategoryId)
            ?? await db.Categories.FirstOrDefaultAsync(c => c.Kind == CategoryKind.Income);
        if (incomeCategory is null)
        {
            return "Error: no Income category is configured.";
        }

        var date = ParseDateOrToday(occurredOn);
        var currency = await GetPreferredCurrencyAsync(db);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CategoryId = incomeCategory.Id,
            Amount = amount,
            Currency = currency,
            OccurredOn = date,
            Description = description,
            Source = TransactionSource.Agent,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return $"Topped up {CurrencyFormatter.Format(amount, currency)} on {date:yyyy-MM-dd}.";
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
            $"{t.OccurredOn:yyyy-MM-dd} | {t.Category?.Name ?? "(uncategorized)"} | {CurrencyFormatter.Format(t.Amount, t.Currency)}{(t.Description is null ? "" : $" | {t.Description}")}");

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

        var currency = await GetPreferredCurrencyAsync(db);
        var exchangeRateService = scope.ServiceProvider.GetRequiredService<IExchangeRateService>();

        // Informational only — SetBudgetAsync still writes even when this pushes the month's total
        // allocation over income (see BudgetRepository.GetAllocationSummaryAsync's own doc comment for why
        // this is deliberately never a blocking check).
        var allocation = await BudgetRepository.GetAllocationSummaryAsync(db, userId, period, currency, exchangeRateService);
        var overNote = allocation.IsOverAllocated
            ? $" Note: total allocated for {period:yyyy-MM} now exceeds income by {CurrencyFormatter.Format(-allocation.AvailableToAllocate, currency)}."
            : "";

        return $"Set {category.Name} budget for {period:yyyy-MM} to {CurrencyFormatter.Format(limitAmount, currency)}.{overNote}";
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
        var currency = await GetPreferredCurrencyAsync(db);

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
                  string.Join(", ", alternatives.Select(b => $"{b.Category.Name} ({CurrencyFormatter.Format(b.LimitAmount, currency)})")) + "."
                : "";

            return $"Cannot transfer {CurrencyFormatter.Format(amount, currency)} from {fromCategory.Name} for {period:yyyy-MM} — only " +
                   $"{CurrencyFormatter.Format(available, currency)} available.{suggestion} Ask the user how they'd like to proceed.";
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

        return $"Transferred {CurrencyFormatter.Format(amount, currency)} from {fromCategory.Name} to {toCategory.Name} for {period:yyyy-MM}. " +
               $"{fromCategory.Name} is now {CurrencyFormatter.Format(fromBudget.LimitAmount, currency)}, {toCategory.Name} is now {CurrencyFormatter.Format(toBudget.LimitAmount, currency)}.";
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

        var currency = await GetPreferredCurrencyAsync(db);
        var lines = statuses.Select(s =>
        {
            var flag = s.IsOver ? " [OVER]" : s.IsNear ? " [NEAR]" : "";
            return $"{s.CategoryName}: {CurrencyFormatter.Format(s.SpentAmount, currency)} / {CurrencyFormatter.Format(s.LimitAmount, currency)} ({s.PercentUsed:F0}%){flag}";
        });

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Records a contribution toward a savings goal as a real <see cref="Transaction"/> (Savings category)
    /// and bumps <see cref="SavingsGoal.CurrentAmount"/> in the same <see cref="FinanceDbContext.SaveChangesAsync(CancellationToken)"/>
    /// — atomic, same two-write shape as <see cref="TransferBudgetAsync"/>. Goal lookup relies on
    /// <see cref="FinanceDbContext"/>'s own <c>HasQueryFilter</c> for <see cref="SavingsGoal"/>
    /// (scoped to <paramref name="userId"/> via the <see cref="FixedCurrentUserAccessor"/> this context was
    /// constructed with) — a goal name belonging to another user simply isn't found, same structural
    /// isolation <see cref="FindCategoryAsync"/> already gets for free.
    /// </summary>
    [AgentSkillScript("contribute_to_goal")]
    public async Task<string> ContributeToGoalAsync(string goalName, decimal amount, string? occurredOn)
    {
        if (amount <= 0)
        {
            return "Error: contribution amount must be greater than zero.";
        }

        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider);

        var goal = await db.SavingsGoals.FirstOrDefaultAsync(g => g.Name.ToLower() == goalName.ToLower());
        if (goal is null)
        {
            return $"Error: no savings goal named '{goalName}' found.";
        }

        var date = ParseDateOrToday(occurredOn);
        var currency = await GetPreferredCurrencyAsync(db);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CategoryId = SeedData.SavingsCategoryId,
            Amount = amount,
            Currency = currency,
            OccurredOn = date,
            Description = $"Contribution to \"{goal.Name}\"",
            Source = TransactionSource.Agent,
            CreatedAtUtc = DateTime.UtcNow,
        });
        goal.CurrentAmount += amount;

        await db.SaveChangesAsync();

        return $"Added {CurrencyFormatter.Format(amount, currency)} to \"{goal.Name}\" on {date:yyyy-MM-dd}. New total: {CurrencyFormatter.Format(goal.CurrentAmount, currency)} / {CurrencyFormatter.Format(goal.TargetAmount, currency)}.";
    }

    private async Task<Category?> FindCategoryAsync(FinanceDbContext db, string categoryName) =>
        await db.Categories.FirstOrDefaultAsync(c => c.Name.ToLower() == categoryName.ToLower());

    private static DateOnly ParseDateOrToday(string? isoDate) =>
        isoDate is not null && DateOnly.TryParse(isoDate, CultureInfo.InvariantCulture, out var parsed)
            ? parsed

            : DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly NormalizeToMonthStart(DateOnly date) => new(date.Year, date.Month, 1);
}
