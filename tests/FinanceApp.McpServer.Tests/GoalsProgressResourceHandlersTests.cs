using System.Text.Json;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.McpServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer.Tests;

/// <summary>
/// Calls <see cref="GoalsProgressResourceHandlers"/> directly (no HTTP transport, no subprocess, no LLM,
/// no live <c>RequestContext&lt;T&gt;</c>) — same shape/rationale as
/// <see cref="MonthlySummaryResourceHandlersTests"/>, its sibling skill handler. Proves the resource-handling
/// logic itself (<c>SKILL.md</c> delivery, the <c>current</c> live-computation convention, cross-user
/// isolation) the same way that file proves <c>monthly-summary</c>'s.
/// </summary>
public sealed class GoalsProgressResourceHandlersTests
{
    private static FinanceDbContext CreateDbContext(string dbName, Guid userId) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
            new FixedCurrentUserAccessor(userId));

    private static GoalsProgressResourceHandlers CreateHandlers(out Guid userId, out string dbName)
    {
        userId = Guid.NewGuid();
        dbName = Guid.NewGuid().ToString();
        var db = CreateDbContext(dbName, userId);
        return new GoalsProgressResourceHandlers(db, new FixedCurrentUserAccessor(userId), NullLogger<GoalsProgressResourceHandlers>.Instance);
    }

    [Fact]
    public void IndexEntry_DescribesTheGoalsProgressSkillAsSkillMd()
    {
        var handlers = CreateHandlers(out _, out _);

        var entry = JsonSerializer.SerializeToElement(handlers.IndexEntry);

        Assert.Equal("goals-progress", entry.GetProperty("name").GetString());
        Assert.Equal("skill-md", entry.GetProperty("type").GetString());
        Assert.Equal("skill://goals-progress/SKILL.md", entry.GetProperty("url").GetString());
    }

    [Fact]
    public void ListableResources_IncludesOnlyTheSkillMdResource()
    {
        var handlers = CreateHandlers(out _, out _);

        var resource = Assert.Single(handlers.ListableResources);

        Assert.Equal("skill://goals-progress/SKILL.md", resource.Uri);
    }

    [Theory]
    [InlineData("skill://goals-progress/SKILL.md", true)]
    [InlineData("skill://goals-progress/current", true)]
    [InlineData("skill://monthly-summary/SKILL.md", false)]
    [InlineData("skill://index.json", false)]
    public void CanHandle_OnlyMatchesItsOwnUris(string uri, bool expected)
    {
        var handlers = CreateHandlers(out _, out _);

        Assert.Equal(expected, handlers.CanHandle(uri));
    }

    [Fact]
    public async Task ReadResource_SkillMd_ReturnsFrontmatterNamedGoalsProgress()
    {
        var handlers = CreateHandlers(out _, out _);

        var result = await handlers.ReadResourceAsync("skill://goals-progress/SKILL.md", CancellationToken.None);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
        Assert.Contains("name: goals-progress", text);
    }

    [Fact]
    public async Task ReadResource_Current_ComputesLiveFromTheDatabase()
    {
        var handlers = CreateHandlers(out var userId, out var dbName);

        await using (var seedDb = CreateDbContext(dbName, userId))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.SavingsGoals.Add(new SavingsGoal
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = "New Car",
                TargetAmount = 10000m,
                CurrentAmount = 2500m,
                MonthlyContribution = 500m,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        var result = await handlers.ReadResourceAsync("skill://goals-progress/current", CancellationToken.None);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
        using var doc = JsonDocument.Parse(text);
        var goal = doc.RootElement[0];
        Assert.Equal("New Car", goal.GetProperty("name").GetString());
        Assert.Equal(25m, goal.GetProperty("percentComplete").GetDecimal());
    }

    [Fact]
    public async Task ReadResource_UnknownUri_ThrowsMcpException()
    {
        var handlers = CreateHandlers(out _, out _);

        await Assert.ThrowsAsync<McpException>(() =>
            handlers.ReadResourceAsync("skill://goals-progress/not-a-real-resource", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReadResource_NoAuthenticatedUser_ThrowsMcpException()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = CreateDbContext(dbName, Guid.NewGuid());
        var handlers = new GoalsProgressResourceHandlers(db, new FixedCurrentUserAccessor(null), NullLogger<GoalsProgressResourceHandlers>.Instance);

        await Assert.ThrowsAsync<McpException>(() =>
            handlers.ReadResourceAsync("skill://goals-progress/current", CancellationToken.None).AsTask());
    }

    /// <summary>
    /// Mirrors <c>MonthlySummaryResourceHandlersTests.ReadResource_ConcurrentRequestsFromDifferentUsers_NeverCrossContaminate</c>
    /// — pins down the query logic; the real per-request DI-scope plumbing is covered by
    /// <c>McpServerConcurrentIsolationTests</c> via a real <c>WebApplicationFactory&lt;Program&gt;</c> host.
    /// </summary>
    [Fact]
    public async Task ReadResource_ConcurrentRequestsFromDifferentUsers_NeverCrossContaminate()
    {
        var dbName = Guid.NewGuid().ToString();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(dbName, userA))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.SavingsGoals.Add(new SavingsGoal { Id = Guid.NewGuid(), UserId = userA, Name = "A's Goal", TargetAmount = 100m, CurrentAmount = 11m, CreatedAtUtc = DateTime.UtcNow });
            seedDb.SavingsGoals.Add(new SavingsGoal { Id = Guid.NewGuid(), UserId = userB, Name = "B's Goal", TargetAmount = 100m, CurrentAmount = 22m, CreatedAtUtc = DateTime.UtcNow });
            await seedDb.SaveChangesAsync();
        }

        var handlersA = new GoalsProgressResourceHandlers(CreateDbContext(dbName, userA), new FixedCurrentUserAccessor(userA), NullLogger<GoalsProgressResourceHandlers>.Instance);
        var handlersB = new GoalsProgressResourceHandlers(CreateDbContext(dbName, userB), new FixedCurrentUserAccessor(userB), NullLogger<GoalsProgressResourceHandlers>.Instance);

        var (resultA, resultB) = (
            await Task.WhenAll(
                    handlersA.ReadResourceAsync("skill://goals-progress/current", CancellationToken.None).AsTask(),
                    handlersB.ReadResourceAsync("skill://goals-progress/current", CancellationToken.None).AsTask())
                is [var a, var b] ? (a, b) : throw new InvalidOperationException());

        var textA = Assert.IsType<TextResourceContents>(Assert.Single(resultA.Contents)).Text;
        var textB = Assert.IsType<TextResourceContents>(Assert.Single(resultB.Contents)).Text;

        using var docA = JsonDocument.Parse(textA);
        using var docB = JsonDocument.Parse(textB);

        Assert.Equal("A's Goal", docA.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("B's Goal", docB.RootElement[0].GetProperty("name").GetString());
    }
}
