using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Core.Repositories;

/// <summary>
/// One savings goal's live progress — computed for <c>FinanceApp.McpServer</c>'s <c>goals-progress</c>
/// skill (docs/spec.md §4.4), added because no other skill source in this app can read
/// <see cref="Entities.SavingsGoal"/> rows at all: <c>savings-goals</c>/<c>savings-calculator</c> (file-based)
/// are guidance/calculation-only with no DB access, and <c>BudgetSkill</c>'s goal lookup is a by-name search
/// for contribution, not a listing. <see cref="PercentComplete"/> is 0 when <see cref="TargetAmount"/> is 0
/// (avoids a division-by-zero rather than reporting "infinite" progress — same convention as
/// <see cref="BudgetStatus.PercentUsed"/>). <see cref="ProjectedMonthsRemaining"/> is a simple linear
/// estimate at the current contribution pace, deliberately not compound-interest math — for a growth-aware
/// projection, <c>savings-calculator</c>'s <c>project-savings.py</c> script (docs/spec.md §4.3) is the
/// source of truth, not this repository.
/// </summary>
public sealed record GoalProgress(
    Guid Id,
    string Name,
    decimal TargetAmount,
    decimal CurrentAmount,
    decimal PercentComplete,
    DateOnly? TargetDate,
    decimal? MonthlyContribution,
    int? ProjectedMonthsRemaining);

public static class SavingsGoalRepository
{
    public static async Task<IReadOnlyList<GoalProgress>> GetGoalsProgressAsync(
        FinanceDbContext db,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var goals = await db.SavingsGoals
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);

        return goals.Select(g =>
        {
            var percent = g.TargetAmount > 0m
                ? Math.Round(g.CurrentAmount / g.TargetAmount * 100m, 1)
                : 0m;

            int? monthsRemaining = g.MonthlyContribution is > 0m && g.CurrentAmount < g.TargetAmount
                ? (int)Math.Ceiling((double)((g.TargetAmount - g.CurrentAmount) / g.MonthlyContribution.Value))
                : null;

            return new GoalProgress(
                g.Id,
                g.Name,
                g.TargetAmount,
                g.CurrentAmount,
                percent,
                g.TargetDate,
                g.MonthlyContribution,
                monthsRemaining);
        }).ToList();
    }
}
