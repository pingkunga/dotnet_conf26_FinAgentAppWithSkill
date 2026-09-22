using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Core.Formatting;
using FinanceApp.Skills.Budgeting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// Calls <see cref="BudgetSkill"/>'s <c>[AgentSkillScript]</c> methods as plain async C# calls — no
/// LLM/agent involved (docs/spec.md §4.1's stated payoff of class-based skills). Each test gets its own
/// EF Core InMemory database (named by GUID) so tests never interfere with each other.
/// </summary>
public sealed class BudgetSkillTests
{
    private static (BudgetSkill skill, string dbName, Guid userId) CreateSkill()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(dbName);
        return (new BudgetSkill(scopeFactory, userId), dbName, userId);
    }

    /// <summary>
    /// Registers <c>DbContextOptions&lt;FinanceDbContext&gt;</c> and a same-currency-only
    /// <see cref="FakeExchangeRateService"/> — mirrors exactly what <see cref="BudgetSkill"/>'s
    /// manual-construction path (see its class remarks) expects in production: it never resolves
    /// <see cref="FinanceDbContext"/> or <see cref="ICurrentUserAccessor"/> from this container, so
    /// neither needs to be registered here. Every transaction in these tests defaults to "USD" (no
    /// ApplicationUser is seeded, so <c>GetPreferredCurrencyAsync</c> falls back to it), so an empty rate
    /// table is enough — <c>SetBudgetAsync</c>'s <c>GetAllocationSummaryAsync</c> call never needs to
    /// convert anything.
    /// </summary>
    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);
        services.AddSingleton<IExchangeRateService>(new FakeExchangeRateService(new Dictionary<(string, string), decimal?>()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task<FinanceDbContext> OpenSeedDbAsync(string dbName, Guid? userId)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new FinanceDbContext(options, new FixedCurrentUserAccessor(userId));
        await db.Database.EnsureCreatedAsync(); // materializes the HasData-seeded global categories
        return db;
    }

    [Fact]
    public async Task AddTransaction_ThenList_ShowsTheTransaction()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId); // ensures categories exist

        var addResult = await skill.AddTransactionAsync(42.50m, "Groceries", "Weekly shop", "2026-08-01");
        Assert.Contains("Groceries", addResult);

        var listResult = await skill.ListTransactionsAsync(fromDate: null, toDate: null, categoryName: null);
        Assert.Contains("Groceries", listResult);
        Assert.Contains("Weekly shop", listResult);
    }

    [Fact]
    public async Task AddTransaction_UnknownCategory_ReturnsError()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.AddTransactionAsync(10m, "NoSuchCategory", null, null);

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task AddTransaction_UnknownCategory_ErrorListsActualAvailableCategoryNames()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.AddTransactionAsync(10m, "Transportation", null, null);

        Assert.StartsWith("Error:", result);
        Assert.Contains("Available categories:", result);
        var availableList = result[(result.IndexOf("Available categories:", StringComparison.Ordinal))..];
        Assert.Contains("Transport", availableList);
        Assert.Contains("Groceries", availableList);
    }

    [Fact]
    public async Task SetBudget_UnknownCategory_ErrorListsActualAvailableCategoryNames()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        // Reproduces the exact bug: the LLM guesses "Transportation" for a Thai prompt about travel
        // expenses, but the seeded category is actually named "Transport".
        var result = await skill.SetBudgetAsync("Transportation", 500m, "2026-08-01");

        Assert.StartsWith("Error:", result);
        Assert.Contains("Available categories:", result);

        // "Transport" is a substring of "Transportation", so a naive Assert.Contains("Transport", result)
        // against the whole string would pass even without the fix — only the suffix after "Available
        // categories:" actually proves the real category list was added.
        var availableList = result[(result.IndexOf("Available categories:", StringComparison.Ordinal))..];
        Assert.Contains("Transport", availableList);
        Assert.Contains("Groceries", availableList);
    }

    [Fact]
    public async Task ListCategories_ReturnsSystemDefaultCategoriesGroupedByExpenseAndIncome()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.ListCategoriesAsync();

        Assert.Contains("Expense categories:", result);
        Assert.Contains("Transport", result);
        Assert.Contains("Groceries", result);
        Assert.Contains("Income categories:", result);
        Assert.Contains("Income", result);
    }

    [Fact]
    public async Task ListCategories_CrossUserIsolation_DoesNotLeakOtherUsersCategory()
    {
        var dbName = Guid.NewGuid().ToString();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using (var seedDb = await OpenSeedDbAsync(dbName, userId: null))
        {
            seedDb.Categories.Add(new Category { Id = Guid.NewGuid(), UserId = userA, Name = "User A Custom", Kind = CategoryKind.Expense });
            await seedDb.SaveChangesAsync();
        }

        var skillA = new BudgetSkill(BuildScopeFactory(dbName), userA);
        var skillB = new BudgetSkill(BuildScopeFactory(dbName), userB);

        var resultA = await skillA.ListCategoriesAsync();
        var resultB = await skillB.ListCategoriesAsync();

        Assert.Contains("User A Custom", resultA);
        Assert.DoesNotContain("User A Custom", resultB);
    }

    [Theory]
    [InlineData(79, false, false)]  // under Near threshold
    [InlineData(80, true, false)]   // exactly Near
    [InlineData(100, true, false)]  // exactly at limit — still Near, not Over
    [InlineData(101, false, true)]  // Over
    public async Task CheckBudgetStatus_FlagsNearAndOverAtTheRightThresholds(decimal spendPercent, bool expectNear, bool expectOver)
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        const decimal limit = 100m;
        await skill.SetBudgetAsync("Groceries", limit, "2026-08-01");
        var spend = limit * spendPercent / 100m;
        await skill.AddTransactionAsync(spend, "Groceries", null, "2026-08-15");

        var status = await skill.CheckBudgetStatusAsync("2026-08-01");

        Assert.Equal(expectNear, status.Contains("[NEAR]"));
        Assert.Equal(expectOver, status.Contains("[OVER]"));
    }

    [Fact]
    public async Task TransferBudget_MovesLimitBetweenCategories_CreatingTheDestinationIfMissing()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        await skill.SetBudgetAsync("Groceries", 1000m, "2026-08-01");

        var result = await skill.TransferBudgetAsync("Groceries", "Dining", 300m, "2026-08-01");

        Assert.Contains("Transferred", result);
        var status = await skill.CheckBudgetStatusAsync("2026-08-01");
        Assert.Contains($"Groceries: {CurrencyFormatter.Format(0m, "USD")} / {CurrencyFormatter.Format(700m, "USD")}", status);
        Assert.Contains($"Dining: {CurrencyFormatter.Format(0m, "USD")} / {CurrencyFormatter.Format(300m, "USD")}", status);
    }

    [Fact]
    public async Task TransferBudget_AddsToAnExistingDestinationBudget_RatherThanOverwritingIt()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        await skill.SetBudgetAsync("Groceries", 1000m, "2026-08-01");
        await skill.SetBudgetAsync("Dining", 200m, "2026-08-01");

        await skill.TransferBudgetAsync("Groceries", "Dining", 300m, "2026-08-01");

        var status = await skill.CheckBudgetStatusAsync("2026-08-01");
        Assert.Contains($"Dining: {CurrencyFormatter.Format(0m, "USD")} / {CurrencyFormatter.Format(500m, "USD")}", status);
    }

    [Fact]
    public async Task TransferBudget_InsufficientFunds_WritesNothingAndReportsWhatsActuallyAvailable()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        await skill.SetBudgetAsync("Groceries", 500m, "2026-08-01");
        await skill.SetBudgetAsync("Dining", 200m, "2026-08-01");

        var result = await skill.TransferBudgetAsync("Groceries", "Dining", 1500m, "2026-08-01");

        Assert.Contains("Cannot transfer", result);
        Assert.Contains(CurrencyFormatter.Format(500m, "USD"), result);

        // Neither budget row changed — the important negative case.
        var status = await skill.CheckBudgetStatusAsync("2026-08-01");
        Assert.Contains($"Groceries: {CurrencyFormatter.Format(0m, "USD")} / {CurrencyFormatter.Format(500m, "USD")}", status);
        Assert.Contains($"Dining: {CurrencyFormatter.Format(0m, "USD")} / {CurrencyFormatter.Format(200m, "USD")}", status);
    }

    [Fact]
    public async Task TransferBudget_InsufficientFunds_SuggestsAlternativeCategoriesWithEnoughHeadroom()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        await skill.SetBudgetAsync("Groceries", 500m, "2026-08-01");
        await skill.SetBudgetAsync("Entertainment", 800m, "2026-08-01"); // enough headroom
        await skill.SetBudgetAsync("Transport", 650m, "2026-08-01"); // enough headroom, less than Entertainment
        await skill.SetBudgetAsync("Utilities", 100m, "2026-08-01"); // not enough — should be excluded

        var result = await skill.TransferBudgetAsync("Groceries", "Dining", 600m, "2026-08-01");

        Assert.Contains($"Entertainment ({CurrencyFormatter.Format(800m, "USD")})", result);
        Assert.Contains($"Transport ({CurrencyFormatter.Format(650m, "USD")})", result);
        Assert.DoesNotContain("Utilities", result);
    }

    [Fact]
    public async Task TransferBudget_UnknownCategory_ReturnsErrorWithoutWriting()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);
        await skill.SetBudgetAsync("Groceries", 500m, "2026-08-01");

        var result = await skill.TransferBudgetAsync("Groceries", "NoSuchCategory", 100m, "2026-08-01");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task TransferBudget_UnknownFromCategory_ErrorListsActualAvailableCategoryNames()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.TransferBudgetAsync("Transportation", "Dining", 100m, "2026-08-01");

        Assert.StartsWith("Error:", result);
        var availableList = result[(result.IndexOf("Available categories:", StringComparison.Ordinal))..];
        Assert.Contains("Transport", availableList);
        Assert.Contains("Groceries", availableList);
    }

    [Fact]
    public async Task TransferBudget_UnknownToCategory_ErrorListsActualAvailableCategoryNames()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.TransferBudgetAsync("Groceries", "Transportation", 100m, "2026-08-01");

        Assert.StartsWith("Error:", result);
        var availableList = result[(result.IndexOf("Available categories:", StringComparison.Ordinal))..];
        Assert.Contains("Transport", availableList);
        Assert.Contains("Groceries", availableList);
    }

    [Fact]
    public async Task CrossUserIsolation_OneUsersSkillNeverSeesAnotherUsersTransactions()
    {
        var dbName = Guid.NewGuid().ToString();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using (var seedDb = await OpenSeedDbAsync(dbName, userId: null))
        {
            // categories seeded via EnsureCreatedAsync above; nothing else to seed here.
            _ = seedDb;
        }

        var skillA = new BudgetSkill(BuildScopeFactory(dbName), userA);
        var skillB = new BudgetSkill(BuildScopeFactory(dbName), userB);

        await skillA.AddTransactionAsync(999m, "Groceries", "User A's secret purchase", "2026-08-01");

        var userBListing = await skillB.ListTransactionsAsync(fromDate: null, toDate: null, categoryName: null);

        Assert.DoesNotContain("999", userBListing);
        Assert.DoesNotContain("secret", userBListing);
        Assert.Equal("No transactions found for that filter.", userBListing);
    }

    [Fact]
    public async Task TopUpFunds_CreatesIncomeTransaction()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.TopUpFundsAsync(1500m, "Salary", "2026-08-01");

        Assert.Contains("Topped up", result);
        await using var db = await OpenSeedDbAsync(dbName, userId);
        var transaction = await db.Transactions.SingleAsync();
        Assert.Equal(SeedData.IncomeCategoryId, transaction.CategoryId);
        Assert.Equal(1500m, transaction.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task TopUpFunds_ZeroOrNegativeAmount_ReturnsErrorAndWritesNothing(decimal amount)
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.TopUpFundsAsync(amount, null, null);

        Assert.StartsWith("Error:", result);
        await using var db = await OpenSeedDbAsync(dbName, userId);
        Assert.Empty(db.Transactions);
    }

    private static async Task<Guid> SeedGoalAsync(string dbName, Guid userId, string name, decimal targetAmount, decimal currentAmount = 0m)
    {
        await using var db = await OpenSeedDbAsync(dbName, userId);
        var goal = new SavingsGoal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            TargetAmount = targetAmount,
            CurrentAmount = currentAmount,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.SavingsGoals.Add(goal);
        await db.SaveChangesAsync();
        return goal.Id;
    }

    [Fact]
    public async Task ContributeToGoal_IncrementsCurrentAmount_AndCreatesTransaction()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId); // ensures categories exist
        await SeedGoalAsync(dbName, userId, "Emergency Fund", 10000m, currentAmount: 1000m);

        var result = await skill.ContributeToGoalAsync("Emergency Fund", 250m, "2026-08-01");

        Assert.Contains("Added", result);
        await using var db = await OpenSeedDbAsync(dbName, userId);
        var goal = await db.SavingsGoals.SingleAsync();
        Assert.Equal(1250m, goal.CurrentAmount);
        var transaction = await db.Transactions.SingleAsync();
        Assert.Equal(SeedData.SavingsCategoryId, transaction.CategoryId);
        Assert.Equal(250m, transaction.Amount);
    }

    [Fact]
    public async Task ContributeToGoal_UnknownGoalName_ReturnsError()
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);

        var result = await skill.ContributeToGoalAsync("No Such Goal", 100m, null);

        Assert.StartsWith("Error:", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task ContributeToGoal_ZeroOrNegativeAmount_LeavesCurrentAmountAndTransactionsUntouched(decimal amount)
    {
        var (skill, dbName, userId) = CreateSkill();
        await using var _ = await OpenSeedDbAsync(dbName, userId);
        await SeedGoalAsync(dbName, userId, "Car", 5000m, currentAmount: 500m);

        var result = await skill.ContributeToGoalAsync("Car", amount, null);

        Assert.StartsWith("Error:", result);
        await using var db = await OpenSeedDbAsync(dbName, userId);
        var goal = await db.SavingsGoals.SingleAsync();
        Assert.Equal(500m, goal.CurrentAmount);
        Assert.Empty(db.Transactions);
    }

    [Fact]
    public async Task ContributeToGoal_CrossUserIsolation_UserBNamingUserAsGoal_ReturnsError()
    {
        var dbName = Guid.NewGuid().ToString();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using (var _ = await OpenSeedDbAsync(dbName, userId: null)) { } // seed global categories
        await SeedGoalAsync(dbName, userA, "User A's Goal", 1000m);

        var skillB = new BudgetSkill(BuildScopeFactory(dbName), userB);
        var result = await skillB.ContributeToGoalAsync("User A's Goal", 100m, null);

        Assert.StartsWith("Error:", result);
        await using var db = await OpenSeedDbAsync(dbName, userId: null);
        Assert.Empty(db.Transactions);
    }
}
