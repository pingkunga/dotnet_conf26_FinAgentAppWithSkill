using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// <see cref="BudgetRepository.GetBudgetStatusAsync"/> — regression coverage for the N+1-to-grouped-query
/// rewrite (Dashboard slowness fix). No prior test exercised this method's output shape directly; existing
/// coverage only went through <c>BudgetSkillTests</c>' <c>check_budget_status</c> / <c>MonthlySummaryRepositoryTests</c>.
/// Same EF Core InMemory split as <see cref="BudgetAllocationSummaryTests"/>.
/// </summary>
public sealed class BudgetStatusTests
{
    private static async Task<FinanceDbContext> SeedDbAsync(string dbName, Guid userId)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new FinanceDbContext(options, new FixedCurrentUserAccessor(userId));
        await db.Database.EnsureCreatedAsync(); // materializes the HasData-seeded global categories
        return db;
    }

    [Fact]
    public async Task GetBudgetStatus_MultipleCategories_AttributesSpendToTheCorrectCategory()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Budgets.AddRange(
            new Budget { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 500m },
            new Budget { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.SavingsCategoryId, PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 200m });
        db.Transactions.AddRange(
            new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, Amount = 300m, OccurredOn = new DateOnly(2026, 8, 5), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow },
            new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, Amount = 50m, OccurredOn = new DateOnly(2026, 8, 10), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow },
            new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.SavingsCategoryId, Amount = 250m, OccurredOn = new DateOnly(2026, 8, 12), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var statuses = await BudgetRepository.GetBudgetStatusAsync(db, userId, new DateOnly(2026, 8, 15));

        Assert.Equal(2, statuses.Count);
        var groceries = Assert.Single(statuses, s => s.CategoryId == SeedData.GroceriesCategoryId);
        Assert.Equal(350m, groceries.SpentAmount);
        Assert.Equal(70m, groceries.PercentUsed);
        Assert.False(groceries.IsOver);

        var savings = Assert.Single(statuses, s => s.CategoryId == SeedData.SavingsCategoryId);
        Assert.Equal(250m, savings.SpentAmount);
        Assert.True(savings.IsOver);
    }

    [Fact]
    public async Task GetBudgetStatus_CategoryWithNoTransactionsThisPeriod_SpentAmountIsZero_NotMissing()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Budgets.Add(new Budget { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 500m });
        await db.SaveChangesAsync();

        var statuses = await BudgetRepository.GetBudgetStatusAsync(db, userId, new DateOnly(2026, 8, 1));

        var status = Assert.Single(statuses);
        Assert.Equal(0m, status.SpentAmount);
        Assert.Equal(0m, status.PercentUsed);
        Assert.False(status.IsOver);
        Assert.False(status.IsNear);
    }

    [Fact]
    public async Task GetBudgetStatus_ZeroLimit_PercentUsedIsZero_NoDivideByZero()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Budgets.Add(new Budget { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 0m });
        db.Transactions.Add(new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, Amount = 100m, OccurredOn = new DateOnly(2026, 8, 5), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var statuses = await BudgetRepository.GetBudgetStatusAsync(db, userId, new DateOnly(2026, 8, 1));

        var status = Assert.Single(statuses);
        Assert.Equal(0m, status.PercentUsed);
    }

    [Theory]
    [InlineData(80, true, false)]
    [InlineData(100, true, false)]
    [InlineData(100.01, false, true)]
    [InlineData(50, false, false)]
    public async Task GetBudgetStatus_NearAndOverBoundaries(double spendFraction, bool expectedNear, bool expectedOver)
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        const decimal limit = 1000m;
        var spent = limit * (decimal)spendFraction / 100m;

        db.Budgets.Add(new Budget { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = limit });
        db.Transactions.Add(new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId, Amount = spent, OccurredOn = new DateOnly(2026, 8, 5), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var statuses = await BudgetRepository.GetBudgetStatusAsync(db, userId, new DateOnly(2026, 8, 1));

        var status = Assert.Single(statuses);
        Assert.Equal(expectedNear, status.IsNear);
        Assert.Equal(expectedOver, status.IsOver);
    }
}
