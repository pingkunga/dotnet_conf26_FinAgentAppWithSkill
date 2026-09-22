using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Core.Repositories;

/// <summary>
/// One category's budget-vs-spend picture for a given month. <see cref="PercentUsed"/> is 0 when
/// <see cref="LimitAmount"/> is 0 (avoids a division-by-zero rather than reporting "infinite" usage).
/// </summary>
public sealed record BudgetStatus(
    Guid CategoryId,
    string CategoryName,
    decimal LimitAmount,
    decimal SpentAmount,
    decimal PercentUsed,
    bool IsNear,
    bool IsOver);

/// <summary>
/// This month's income vs. total budget allocation, for the "available to allocate" figure surfaced on
/// <c>Budgets.razor</c>/<c>Home.razor</c> and in <c>BudgetSkill.SetBudgetAsync</c>'s chat response.
/// <see cref="AllocatedTotal"/> only ever sums <see cref="Entities.Budget"/> rows whose category is
/// <see cref="Entities.CategoryKind.Expense"/> — an Income-kind category's budget (nothing prevents one
/// being created today) would otherwise double-count against income instead of allocating it.
/// </summary>
/// <remarks>
/// <see cref="IncomeTotal"/> is expressed in whatever currency was passed as <c>targetCurrency</c> to
/// <see cref="BudgetRepository.GetAllocationSummaryAsync"/> — each transaction's own <see cref="Entities.Transaction.Currency"/>
/// is converted to it via <see cref="IExchangeRateService"/>. <see cref="AllocatedTotal"/> (from
/// <see cref="Entities.Budget.LimitAmount"/>) is treated as already being in that currency — <c>Budget</c>
/// has no currency column of its own. <see cref="ConversionIncomplete"/> is <c>true</c> when at least one
/// currency present among the user's income transactions couldn't be converted (rate lookup failed) — that
/// currency's amount is excluded from <see cref="IncomeTotal"/> entirely, never guessed at a 1:1 rate.
/// </remarks>
public sealed record BudgetAllocationSummary(
    DateOnly PeriodMonth,
    decimal IncomeTotal,
    decimal AllocatedTotal,
    decimal AvailableToAllocate,
    bool IsOverAllocated,
    bool ConversionIncomplete);

/// <summary>
/// Budget-status computation shared by <c>BudgetSkill.CheckBudgetStatusAsync</c> (docs/spec.md §4.1) and,
/// eventually, the Budgets CRUD page (§7) — factored here so the UI and the skill can never drift apart.
/// </summary>
public static class BudgetRepository
{
    /// <summary>
    /// Near: spend is 80–100% of the limit. Over: spend exceeds the limit (docs/spec.md §4.1).
    /// Computes spend for all budgeted categories in one grouped query (not one query per category) —
    /// a prior per-category <c>SumAsync</c> loop here was a confirmed N+1 (Home.razor's Dashboard stacking
    /// this on top of 3 more sequential queries was the concrete symptom that surfaced it).
    /// </summary>
    public static async Task<IReadOnlyList<BudgetStatus>> GetBudgetStatusAsync(
        FinanceDbContext db,
        Guid userId,
        DateOnly periodMonth,
        CancellationToken cancellationToken = default)
    {
        var normalizedPeriod = new DateOnly(periodMonth.Year, periodMonth.Month, 1);
        var periodEnd = normalizedPeriod.AddMonths(1);

        var budgets = await db.Budgets
            .Where(b => b.UserId == userId && b.PeriodMonth == normalizedPeriod)
            .Include(b => b.Category)
            .ToListAsync(cancellationToken);

        if (budgets.Count == 0)
        {
            return [];
        }

        var categoryIds = budgets.Select(b => b.CategoryId).ToList();
        var spentByCategory = await db.Transactions
            .Where(t => t.UserId == userId
                && t.CategoryId != null && categoryIds.Contains(t.CategoryId.Value)
                && t.OccurredOn >= normalizedPeriod
                && t.OccurredOn < periodEnd)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Total, cancellationToken);

        var results = new List<BudgetStatus>(budgets.Count);
        foreach (var budget in budgets)
        {
            var spent = spentByCategory.GetValueOrDefault(budget.CategoryId, 0m);
            var percent = budget.LimitAmount == 0m ? 0m : spent / budget.LimitAmount * 100m;

            results.Add(new BudgetStatus(
                budget.CategoryId,
                budget.Category.Name,
                budget.LimitAmount,
                spent,
                percent,
                IsNear: percent is >= 80m and <= 100m,
                IsOver: percent > 100m));
        }

        return results;
    }

    /// <summary>
    /// This month's income total vs. total budget allocation across all categories — the "available to
    /// allocate" figure (docs/spec.md's top-up flow). Warn-only by design: income arrives mid-month, so a
    /// legitimate budget set on day 2 will exceed income received so far — this is informational, never a
    /// write-blocking check.
    /// </summary>
    public static async Task<BudgetAllocationSummary> GetAllocationSummaryAsync(
        FinanceDbContext db,
        Guid userId,
        DateOnly periodMonth,
        string targetCurrency,
        IExchangeRateService exchangeRateService,
        CancellationToken cancellationToken = default)
    {
        var normalizedPeriod = new DateOnly(periodMonth.Year, periodMonth.Month, 1);
        var periodEnd = normalizedPeriod.AddMonths(1);

        var incomeByCurrency = await db.Transactions
            .Where(t => t.UserId == userId
                && t.Category != null && t.Category.Kind == CategoryKind.Income
                && t.OccurredOn >= normalizedPeriod
                && t.OccurredOn < periodEnd)
            .GroupBy(t => t.Currency)
            .Select(g => new { Currency = g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync(cancellationToken);

        var incomeTotal = 0m;
        var conversionIncomplete = false;
        foreach (var group in incomeByCurrency)
        {
            var rate = group.Currency == targetCurrency
                ? 1m
                : await exchangeRateService.GetRateAsync(group.Currency, targetCurrency, cancellationToken);

            if (rate is null)
            {
                // Excluded, not guessed at 1:1 — see BudgetAllocationSummary.ConversionIncomplete's doc comment.
                conversionIncomplete = true;
                continue;
            }

            incomeTotal += group.Total * rate.Value;
        }

        // Budget has no Currency column — treated as already being in targetCurrency (docs/spec.md's known
        // limitations; see BudgetAllocationSummary's doc comment).
        var allocatedTotal = await db.Budgets
            .Where(b => b.UserId == userId
                && b.PeriodMonth == normalizedPeriod
                && b.Category.Kind == CategoryKind.Expense)
            .SumAsync(b => (decimal?)b.LimitAmount, cancellationToken) ?? 0m;

        var available = incomeTotal - allocatedTotal;

        return new BudgetAllocationSummary(normalizedPeriod, incomeTotal, allocatedTotal, available, available < 0m, conversionIncomplete);
    }
}
