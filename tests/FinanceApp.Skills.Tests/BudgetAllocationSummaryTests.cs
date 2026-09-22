using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// <see cref="BudgetRepository.GetAllocationSummaryAsync"/> — the "available to allocate" figure surfaced
/// on <c>Budgets.razor</c>/<c>Home.razor</c> and in <c>BudgetSkill.SetBudgetAsync</c>'s chat response.
/// Tested here directly against EF Core InMemory, same split as <see cref="MonthlySummaryRepositoryTests"/>.
/// </summary>
public sealed class BudgetAllocationSummaryTests
{
    private static async Task<FinanceDbContext> SeedDbAsync(string dbName, Guid userId)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new FinanceDbContext(options, new FixedCurrentUserAccessor(userId));
        await db.Database.EnsureCreatedAsync(); // materializes the HasData-seeded global categories
        return db;
    }

    [Fact]
    public async Task GetAllocationSummary_SumsIncomeAndExpenseBudgetsSeparately()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            Amount = 3000m, OccurredOn = new DateOnly(2026, 8, 1), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow,
        });
        db.Budgets.Add(new Budget
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId,
            PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 500m,
        });
        await db.SaveChangesAsync();

        var summary = await BudgetRepository.GetAllocationSummaryAsync(
            db, userId, new DateOnly(2026, 8, 15), "USD", NoOpExchangeRateService);

        Assert.Equal(3000m, summary.IncomeTotal);
        Assert.Equal(500m, summary.AllocatedTotal);
        Assert.Equal(2500m, summary.AvailableToAllocate);
        Assert.False(summary.IsOverAllocated);
        Assert.False(summary.ConversionIncomplete);
        Assert.Equal(new DateOnly(2026, 8, 1), summary.PeriodMonth);
    }

    [Fact]
    public async Task GetAllocationSummary_ExcludesAnIncomeKindCategorysBudgetFromAllocatedTotal()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        // Nothing in the UI prevents a Budget row against the Income category — the repository must not
        // let it double-count against income.
        db.Budgets.Add(new Budget
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 999m,
        });
        db.Budgets.Add(new Budget
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId,
            PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 200m,
        });
        await db.SaveChangesAsync();

        var summary = await BudgetRepository.GetAllocationSummaryAsync(
            db, userId, new DateOnly(2026, 8, 1), "USD", NoOpExchangeRateService);

        Assert.Equal(200m, summary.AllocatedTotal);
    }

    [Fact]
    public async Task GetAllocationSummary_AllocatedExceedsIncome_IsOverAllocatedTrue()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            Amount = 100m, OccurredOn = new DateOnly(2026, 8, 1), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow,
        });
        db.Budgets.Add(new Budget
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId,
            PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 150m,
        });
        await db.SaveChangesAsync();

        var summary = await BudgetRepository.GetAllocationSummaryAsync(
            db, userId, new DateOnly(2026, 8, 1), "USD", NoOpExchangeRateService);

        Assert.Equal(-50m, summary.AvailableToAllocate);
        Assert.True(summary.IsOverAllocated);
    }

    [Fact]
    public async Task GetAllocationSummary_AllocatedEqualsIncome_IsOverAllocatedFalse()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            Amount = 100m, OccurredOn = new DateOnly(2026, 8, 1), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow,
        });
        db.Budgets.Add(new Budget
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.GroceriesCategoryId,
            PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 100m,
        });
        await db.SaveChangesAsync();

        var summary = await BudgetRepository.GetAllocationSummaryAsync(
            db, userId, new DateOnly(2026, 8, 1), "USD", NoOpExchangeRateService);

        Assert.Equal(0m, summary.AvailableToAllocate);
        Assert.False(summary.IsOverAllocated);
    }

    [Fact]
    public async Task GetAllocationSummary_ConvertsNonTargetCurrencyIncomeUsingTheExchangeRateService()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            Amount = 1000m, Currency = "USD", OccurredOn = new DateOnly(2026, 8, 1), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow,
        });
        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            Amount = 3500m, Currency = "THB", OccurredOn = new DateOnly(2026, 8, 2), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var exchangeRateService = new FakeExchangeRateService(new Dictionary<(string, string), decimal?>
        {
            [("THB", "USD")] = 0.02m,
        });

        var summary = await BudgetRepository.GetAllocationSummaryAsync(
            db, userId, new DateOnly(2026, 8, 1), "USD", exchangeRateService);

        // 1000 USD (rate 1) + 3500 THB * 0.02 = 1000 + 70 = 1070
        Assert.Equal(1070m, summary.IncomeTotal);
        Assert.False(summary.ConversionIncomplete);
    }

    [Fact]
    public async Task GetAllocationSummary_UnconvertibleCurrency_ExcludesItAndFlagsConversionIncomplete()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using var db = await SeedDbAsync(dbName, userId);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            Amount = 1000m, Currency = "USD", OccurredOn = new DateOnly(2026, 8, 1), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow,
        });
        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, CategoryId = SeedData.IncomeCategoryId,
            Amount = 3500m, Currency = "THB", OccurredOn = new DateOnly(2026, 8, 2), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // No THB->USD rate registered — GetRateAsync returns null for that pair.
        var exchangeRateService = new FakeExchangeRateService(new Dictionary<(string, string), decimal?>());

        var summary = await BudgetRepository.GetAllocationSummaryAsync(
            db, userId, new DateOnly(2026, 8, 1), "USD", exchangeRateService);

        // The unconvertible THB amount is excluded entirely, not guessed at 1:1.
        Assert.Equal(1000m, summary.IncomeTotal);
        Assert.True(summary.ConversionIncomplete);
    }

    private static readonly FakeExchangeRateService NoOpExchangeRateService = new(new Dictionary<(string, string), decimal?>());
}
