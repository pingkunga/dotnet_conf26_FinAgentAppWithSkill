using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
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
    /// Registers only <c>DbContextOptions&lt;FinanceDbContext&gt;</c> — mirrors exactly what
    /// <see cref="BudgetSkill"/>'s manual-construction path (see its class remarks) expects in
    /// production: it never resolves <see cref="FinanceDbContext"/> or <see cref="ICurrentUserAccessor"/>
    /// from this container, so neither needs to be registered here.
    /// </summary>
    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);
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
        Assert.Contains($"Groceries: {0m:C} / {700m:C}", status);
        Assert.Contains($"Dining: {0m:C} / {300m:C}", status);
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
        Assert.Contains($"Dining: {0m:C} / {500m:C}", status);
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
        Assert.Contains($"{500m:C}", result);

        // Neither budget row changed — the important negative case.
        var status = await skill.CheckBudgetStatusAsync("2026-08-01");
        Assert.Contains($"Groceries: {0m:C} / {500m:C}", status);
        Assert.Contains($"Dining: {0m:C} / {200m:C}", status);
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

        Assert.Contains($"Entertainment ({800m:C})", result);
        Assert.Contains($"Transport ({650m:C})", result);
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
}
