using FinanceApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Core.Repositories;

/// <summary>One category's total for a given month, alongside its <see cref="Category.Kind"/>.</summary>
public sealed record CategoryTotal(Guid CategoryId, string CategoryName, CategoryKind Kind, decimal TotalAmount);

/// <summary>
/// A month's income/expense picture for one user — computed by <see cref="MonthlySummaryRepository"/> and
/// consumed by <c>FinanceApp.McpServer</c>'s <c>read_skill_resource</c> handler (docs/spec.md §4.4). Shared
/// here (not in McpServer itself) so it's testable with EF Core InMemory, the same bar every other
/// skill-adjacent computation in this app is held to.
/// </summary>
public sealed record MonthlySummary(
    DateOnly PeriodMonth,
    decimal TotalIncome,
    decimal TotalExpense,
    IReadOnlyList<CategoryTotal> ByCategory,
    IReadOnlyList<BudgetStatus> BudgetStatuses);

/// <summary>
/// Monthly summary computation for the MCP-based skill (docs/spec.md §4.4) — the 4th and last skill
/// source type. Reuses <see cref="BudgetRepository.GetBudgetStatusAsync"/> rather than recomputing
/// budget-vs-spend itself, so the two never drift apart (same reasoning as <c>BudgetRepository</c>'s own
/// remarks).
/// </summary>
public static class MonthlySummaryRepository
{
    public static async Task<MonthlySummary> GetMonthlySummaryAsync(
        FinanceDbContext db,
        Guid userId,
        DateOnly periodMonth,
        CancellationToken cancellationToken = default)
    {
        var normalizedPeriod = new DateOnly(periodMonth.Year, periodMonth.Month, 1);
        var periodEnd = normalizedPeriod.AddMonths(1);

        var transactions = await db.Transactions
            .Include(t => t.Category)
            .Where(t => t.UserId == userId && t.OccurredOn >= normalizedPeriod && t.OccurredOn < periodEnd)
            .ToListAsync(cancellationToken);

        var totalIncome = transactions
            .Where(t => t.Category?.Kind == CategoryKind.Income)
            .Sum(t => t.Amount);

        var totalExpense = transactions
            .Where(t => t.Category?.Kind == CategoryKind.Expense)
            .Sum(t => t.Amount);

        var byCategory = transactions
            .Where(t => t.Category is not null)
            .GroupBy(t => t.Category!)
            .Select(g => new CategoryTotal(g.Key.Id, g.Key.Name, g.Key.Kind, g.Sum(t => t.Amount)))
            .OrderByDescending(c => c.TotalAmount)
            .ToList();

        var budgetStatuses = await BudgetRepository.GetBudgetStatusAsync(db, userId, normalizedPeriod, cancellationToken);

        return new MonthlySummary(normalizedPeriod, totalIncome, totalExpense, byCategory, budgetStatuses);
    }
}
