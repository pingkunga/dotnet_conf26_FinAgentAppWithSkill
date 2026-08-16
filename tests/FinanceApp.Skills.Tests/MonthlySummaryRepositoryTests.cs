using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// <see cref="MonthlySummaryRepository"/> is the computation <c>FinanceApp.McpServer</c>'s
/// <c>read_skill_resource</c> handler wraps for the MCP-based monthly-summary skill (docs/spec.md §4.4).
/// Tested here directly against EF Core InMemory — no MCP transport, no subprocess — same "prove the
/// computation, not the transport" split as <c>ReceiptOcrSkillFactoryTests</c>.
/// </summary>
public sealed class MonthlySummaryRepositoryTests
{
    private static async Task<(FinanceDbContext db, Guid userId, Guid groceriesId, Guid salaryId)> SeedAsync(string dbName)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options;
        var userId = Guid.NewGuid();
        var db = new FinanceDbContext(options, new FixedCurrentUserAccessor(userId));
        await db.Database.EnsureCreatedAsync(); // materializes the HasData-seeded global categories

        var groceries = await db.Categories.FirstAsync(c => c.Name == "Groceries" && c.Kind == CategoryKind.Expense);
        var salary = new Category { Id = Guid.NewGuid(), UserId = userId, Name = "Salary", Kind = CategoryKind.Income };
        db.Categories.Add(salary);
        await db.SaveChangesAsync();

        return (db, userId, groceries.Id, salary.Id);
    }

    [Fact]
    public async Task GetMonthlySummary_TotalsIncomeAndExpenseSeparately()
    {
        var (db, userId, groceriesId, salaryId) = await SeedAsync(Guid.NewGuid().ToString());
        await using var _ = db;

        db.Transactions.AddRange(
            new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = groceriesId, Amount = 40m, OccurredOn = new DateOnly(2026, 8, 5), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow },
            new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = salaryId, Amount = 3000m, OccurredOn = new DateOnly(2026, 8, 1), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow },
            // outside the requested month — must not be counted
            new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = groceriesId, Amount = 999m, OccurredOn = new DateOnly(2026, 7, 31), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var summary = await MonthlySummaryRepository.GetMonthlySummaryAsync(db, userId, new DateOnly(2026, 8, 15));

        Assert.Equal(3000m, summary.TotalIncome);
        Assert.Equal(40m, summary.TotalExpense);
        Assert.Equal(new DateOnly(2026, 8, 1), summary.PeriodMonth);
        Assert.Equal(2, summary.ByCategory.Count);
    }

    [Fact]
    public async Task GetMonthlySummary_IncludesBudgetStatusesForTheSamePeriod()
    {
        var (db, userId, groceriesId, _) = await SeedAsync(Guid.NewGuid().ToString());
        await using var _ = db;

        db.Budgets.Add(new Budget { Id = Guid.NewGuid(), UserId = userId, CategoryId = groceriesId, PeriodMonth = new DateOnly(2026, 8, 1), LimitAmount = 100m });
        db.Transactions.Add(new Transaction { Id = Guid.NewGuid(), UserId = userId, CategoryId = groceriesId, Amount = 120m, OccurredOn = new DateOnly(2026, 8, 10), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var summary = await MonthlySummaryRepository.GetMonthlySummaryAsync(db, userId, new DateOnly(2026, 8, 1));

        var status = Assert.Single(summary.BudgetStatuses);
        Assert.True(status.IsOver);
    }

    [Fact]
    public async Task GetMonthlySummary_NoTransactionsInMonth_ReturnsZeroTotalsAndEmptyCollections()
    {
        var (db, userId, _, _) = await SeedAsync(Guid.NewGuid().ToString());
        await using var _ = db;

        var summary = await MonthlySummaryRepository.GetMonthlySummaryAsync(db, userId, new DateOnly(2026, 8, 1));

        Assert.Equal(0m, summary.TotalIncome);
        Assert.Equal(0m, summary.TotalExpense);
        Assert.Empty(summary.ByCategory);
        Assert.Empty(summary.BudgetStatuses);
    }

    [Fact]
    public async Task GetMonthlySummary_DoesNotIncludeAnotherUsersTransactions()
    {
        var (db, userId, groceriesId, _) = await SeedAsync(Guid.NewGuid().ToString());
        await using var _ = db;

        var otherUserId = Guid.NewGuid();
        db.Transactions.Add(new Transaction { Id = Guid.NewGuid(), UserId = otherUserId, CategoryId = groceriesId, Amount = 500m, OccurredOn = new DateOnly(2026, 8, 10), Source = TransactionSource.Manual, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var summary = await MonthlySummaryRepository.GetMonthlySummaryAsync(db, userId, new DateOnly(2026, 8, 1));

        Assert.Equal(0m, summary.TotalExpense);
        Assert.Empty(summary.ByCategory);
    }
}
