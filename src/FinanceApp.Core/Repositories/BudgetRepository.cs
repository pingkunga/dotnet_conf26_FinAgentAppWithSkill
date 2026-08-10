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
/// Budget-status computation shared by <c>BudgetSkill.CheckBudgetStatusAsync</c> (docs/spec.md §4.1) and,
/// eventually, the Budgets CRUD page (§7) — factored here so the UI and the skill can never drift apart.
/// </summary>
public static class BudgetRepository
{
    /// <summary>Near: spend is 80–100% of the limit. Over: spend exceeds the limit (docs/spec.md §4.1).</summary>
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

        var results = new List<BudgetStatus>(budgets.Count);
        foreach (var budget in budgets)
        {
            var spent = await db.Transactions
                .Where(t => t.UserId == userId
                    && t.CategoryId == budget.CategoryId
                    && t.OccurredOn >= normalizedPeriod
                    && t.OccurredOn < periodEnd)
                .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;

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
}
