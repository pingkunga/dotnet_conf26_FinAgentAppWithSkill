using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// <see cref="SavingsGoalRepository"/> is the computation <c>FinanceApp.McpServer</c>'s
/// <c>GoalsProgressResourceHandlers</c> wraps for the MCP-based goals-progress skill (docs/spec.md §4.4).
/// Tested here directly against EF Core InMemory — same "prove the computation, not the transport" split
/// as <see cref="MonthlySummaryRepositoryTests"/>.
/// </summary>
public sealed class SavingsGoalRepositoryTests
{
    private static FinanceDbContext CreateDbContext(string dbName, Guid userId) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
            new FixedCurrentUserAccessor(userId));

    [Fact]
    public async Task GetGoalsProgress_ComputesPercentCompleteAndProjectedMonthsRemaining()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext(dbName, userId);
        await db.Database.EnsureCreatedAsync();

        db.SavingsGoals.Add(new SavingsGoal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "New Car",
            TargetAmount = 10000m,
            CurrentAmount = 2500m,
            MonthlyContribution = 500m,
            TargetDate = new DateOnly(2027, 1, 1),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var progress = Assert.Single(await SavingsGoalRepository.GetGoalsProgressAsync(db, userId));

        Assert.Equal("New Car", progress.Name);
        Assert.Equal(25m, progress.PercentComplete);
        // (10000 - 2500) / 500 = 15 whole months
        Assert.Equal(15, progress.ProjectedMonthsRemaining);
    }

    [Fact]
    public async Task GetGoalsProgress_NoMonthlyContribution_ProjectedMonthsRemainingIsNull()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext(dbName, userId);
        await db.Database.EnsureCreatedAsync();

        db.SavingsGoals.Add(new SavingsGoal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Rainy Day",
            TargetAmount = 1000m,
            CurrentAmount = 100m,
            MonthlyContribution = null,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var progress = Assert.Single(await SavingsGoalRepository.GetGoalsProgressAsync(db, userId));

        Assert.Null(progress.ProjectedMonthsRemaining);
    }

    [Fact]
    public async Task GetGoalsProgress_GoalAlreadyComplete_ProjectedMonthsRemainingIsNull()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext(dbName, userId);
        await db.Database.EnsureCreatedAsync();

        db.SavingsGoals.Add(new SavingsGoal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Done Already",
            TargetAmount = 500m,
            CurrentAmount = 500m,
            MonthlyContribution = 50m,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var progress = Assert.Single(await SavingsGoalRepository.GetGoalsProgressAsync(db, userId));

        Assert.Equal(100m, progress.PercentComplete);
        Assert.Null(progress.ProjectedMonthsRemaining);
    }

    [Fact]
    public async Task GetGoalsProgress_ZeroTargetAmount_PercentCompleteIsZeroNotDivideByZero()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = CreateDbContext(dbName, userId);
        await db.Database.EnsureCreatedAsync();

        db.SavingsGoals.Add(new SavingsGoal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Malformed",
            TargetAmount = 0m,
            CurrentAmount = 0m,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var progress = Assert.Single(await SavingsGoalRepository.GetGoalsProgressAsync(db, userId));

        Assert.Equal(0m, progress.PercentComplete);
    }

    [Fact]
    public async Task GetGoalsProgress_DoesNotIncludeAnotherUsersGoals()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await using var db = CreateDbContext(dbName, userId);
        await db.Database.EnsureCreatedAsync();

        db.SavingsGoals.Add(new SavingsGoal
        {
            Id = Guid.NewGuid(),
            UserId = otherUserId,
            Name = "Someone Else's Goal",
            TargetAmount = 999m,
            CurrentAmount = 1m,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var progress = await SavingsGoalRepository.GetGoalsProgressAsync(db, userId);

        Assert.Empty(progress);
    }
}
