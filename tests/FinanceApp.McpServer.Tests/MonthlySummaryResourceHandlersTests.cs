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
/// Calls <see cref="MonthlySummaryResourceHandlers"/>'s *Core methods directly — no HTTP transport, no
/// subprocess, no LLM, no live <c>RequestContext&lt;T&gt;</c> (its only constructor needs a real
/// <c>McpServer</c> + <c>JsonRpcRequest</c>, too heavy for a unit test) — proving the resource-handling
/// logic itself (the <c>skill://index.json</c> shape, SKILL.md delivery, and the
/// <c>summary-&lt;year&gt;-&lt;month&gt;</c> live-computation convention, docs/spec.md §4.4) the same way
/// <c>ReceiptOcrSkillFactoryTests</c> calls a skill script directly rather than through the LLM tool-call
/// pipeline. Since Step 1's HTTP-migration (docs/spec.md §5) made <c>MonthlySummaryResourceHandlers</c> a
/// per-request scoped DI service (<c>FinanceDbContext</c> + <c>ICurrentUserAccessor</c> injected directly,
/// no more ctor-captured <c>dbOptions</c>/<c>userId</c> bound once for a whole process), this file also
/// covers the concurrent multi-user isolation property that per-request model depends on.
/// </summary>
public sealed class MonthlySummaryResourceHandlersTests
{
    private static FinanceDbContext CreateDbContext(string dbName, Guid userId) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
            new FixedCurrentUserAccessor(userId));

    private static MonthlySummaryResourceHandlers CreateHandlers(out Guid userId, out string dbName)
    {
        userId = Guid.NewGuid();
        dbName = Guid.NewGuid().ToString();
        var db = CreateDbContext(dbName, userId);
        return new MonthlySummaryResourceHandlers(db, new FixedCurrentUserAccessor(userId), NullLogger<MonthlySummaryResourceHandlers>.Instance);
    }

    [Fact]
    public async Task ReadResource_Index_ListsTheMonthlySummarySkillPointingAtSkillMd()
    {
        var handlers = CreateHandlers(out _, out _);

        var result = await handlers.ReadResourceCoreAsync("skill://index.json", CancellationToken.None);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
        using var doc = JsonDocument.Parse(text);
        var skill = doc.RootElement.GetProperty("skills")[0];
        Assert.Equal("monthly-summary", skill.GetProperty("name").GetString());
        Assert.Equal("skill-md", skill.GetProperty("type").GetString());
        Assert.Equal("skill://monthly-summary/SKILL.md", skill.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ReadResource_SkillMd_ReturnsFrontmatterNamedMonthlySummary()
    {
        var handlers = CreateHandlers(out _, out _);

        var result = await handlers.ReadResourceCoreAsync("skill://monthly-summary/SKILL.md", CancellationToken.None);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
        Assert.Contains("name: monthly-summary", text);
    }

    [Fact]
    public async Task ReadResource_SummaryResource_ComputesLiveFromTheDatabase()
    {
        var handlers = CreateHandlers(out var userId, out var dbName);

        await using (var seedDb = CreateDbContext(dbName, userId))
        {
            await seedDb.Database.EnsureCreatedAsync();
            var groceries = await seedDb.Categories.FirstAsync(c => c.Name == "Groceries" && c.Kind == CategoryKind.Expense);
            seedDb.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CategoryId = groceries.Id,
                Amount = 55m,
                OccurredOn = new DateOnly(2026, 8, 10),
                Source = TransactionSource.Manual,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        var result = await handlers.ReadResourceCoreAsync("skill://monthly-summary/summary-2026-08", CancellationToken.None);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("2026-08", doc.RootElement.GetProperty("month").GetString());
        Assert.Equal(55m, doc.RootElement.GetProperty("totalExpense").GetDecimal());
    }

    [Fact]
    public async Task ReadResource_MalformedResourceName_ThrowsMcpException()
    {
        var handlers = CreateHandlers(out _, out _);

        await Assert.ThrowsAsync<McpException>(() =>
            handlers.ReadResourceCoreAsync("skill://monthly-summary/not-a-valid-name", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReadResource_UnknownUri_ThrowsMcpException()
    {
        var handlers = CreateHandlers(out _, out _);

        await Assert.ThrowsAsync<McpException>(() =>
            handlers.ReadResourceCoreAsync("skill://something-else", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReadResource_NoAuthenticatedUser_ThrowsMcpException()
    {
        // Simulates a request that somehow reached the handler without a resolved userId claim (should
        // never happen in production behind app.MapMcp().RequireAuthorization(), but this is the last line
        // of defense — the isolation boundary now lives per-request, not per-process, so this handler must
        // never silently proceed with no user, docs/spec.md §5's rewritten isolation note).
        var dbName = Guid.NewGuid().ToString();
        var db = CreateDbContext(dbName, Guid.NewGuid());
        var handlers = new MonthlySummaryResourceHandlers(db, new FixedCurrentUserAccessor(null), NullLogger<MonthlySummaryResourceHandlers>.Instance);

        await Assert.ThrowsAsync<McpException>(() =>
            handlers.ReadResourceCoreAsync("skill://monthly-summary/summary-2026-08", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ListResources_IncludesTheIndexAndTheSkillMdResource()
    {
        var result = await MonthlySummaryResourceHandlers.ListResourcesCoreAsync();

        Assert.Contains(result.Resources, r => r.Uri == "skill://index.json");
        Assert.Contains(result.Resources, r => r.Uri == "skill://monthly-summary/SKILL.md");
    }

    /// <summary>
    /// Proves the <b>repository query</b> (<c>MonthlySummaryRepository.GetMonthlySummaryAsync</c>) itself
    /// correctly separates two users' rows in one shared database — necessary, but **not sufficient** to
    /// prove Step 1's HTTP migration's real isolation property (docs/spec.md §5): both handler instances
    /// here are constructed by hand, each with its own <see cref="FixedCurrentUserAccessor"/>, so this path
    /// is structurally incapable of leaking regardless of whether the real per-request DI scoping
    /// (<c>HttpUserContextAccessor</c> + ASP.NET Core's per-HTTP-request scope) actually works. That
    /// property — whether two *overlapping* MCP requests over real HTTP actually get two different DI
    /// scopes — is exercised by <c>McpServerConcurrentIsolationTests</c> instead, via a real
    /// `WebApplicationFactory&lt;Program&gt;` host. Keep both: this one pins down the query logic cheaply,
    /// that one pins down the plumbing in front of it.
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
            var groceries = await seedDb.Categories.FirstAsync(c => c.Name == "Groceries" && c.Kind == CategoryKind.Expense);

            seedDb.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = userA,
                CategoryId = groceries.Id,
                Amount = 111m,
                OccurredOn = new DateOnly(2026, 8, 5),
                Source = TransactionSource.Manual,
                CreatedAtUtc = DateTime.UtcNow,
            });
            seedDb.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = userB,
                CategoryId = groceries.Id,
                Amount = 222m,
                OccurredOn = new DateOnly(2026, 8, 6),
                Source = TransactionSource.Manual,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        // Two independent scoped instances against the same underlying database — exactly what two
        // concurrent HTTP requests get in production (a fresh FinanceDbContext + ICurrentUserAccessor per
        // request, per ASP.NET Core's normal per-request DI scope).
        var handlersA = new MonthlySummaryResourceHandlers(CreateDbContext(dbName, userA), new FixedCurrentUserAccessor(userA), NullLogger<MonthlySummaryResourceHandlers>.Instance);
        var handlersB = new MonthlySummaryResourceHandlers(CreateDbContext(dbName, userB), new FixedCurrentUserAccessor(userB), NullLogger<MonthlySummaryResourceHandlers>.Instance);

        var (resultA, resultB) = (
            await Task.WhenAll(
                    handlersA.ReadResourceCoreAsync("skill://monthly-summary/summary-2026-08", CancellationToken.None).AsTask(),
                    handlersB.ReadResourceCoreAsync("skill://monthly-summary/summary-2026-08", CancellationToken.None).AsTask())
                is [var a, var b] ? (a, b) : throw new InvalidOperationException());

        var textA = Assert.IsType<TextResourceContents>(Assert.Single(resultA.Contents)).Text;
        var textB = Assert.IsType<TextResourceContents>(Assert.Single(resultB.Contents)).Text;

        using var docA = JsonDocument.Parse(textA);
        using var docB = JsonDocument.Parse(textB);

        Assert.Equal(111m, docA.RootElement.GetProperty("totalExpense").GetDecimal());
        Assert.Equal(222m, docB.RootElement.GetProperty("totalExpense").GetDecimal());
    }
}
