using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FinanceApp.Core;
using FinanceApp.Core.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer.Tests;

/// <summary>
/// Real end-to-end proof of the property Step 1's HTTP migration actually depends on (docs/spec.md §5):
/// two concurrent HTTP requests, carrying two different users' bearer tokens, must resolve through the
/// **real** `HttpUserContextAccessor` + JwtBearer + scoped-DI pipeline to two independently-scoped
/// `FinanceDbContext` instances and never cross-contaminate. <see cref="MonthlySummaryResourceHandlersTests"/>'s
/// concurrent test constructs handler instances by hand with two <c>FixedCurrentUserAccessor</c>s — that
/// proves the repository query itself separates users correctly, but that path is structurally incapable
/// of leaking regardless of whether the real per-request DI scoping works. This test is the one that
/// actually exercises the new risk surface: whether <c>RequestContext&lt;T&gt;.Services</c> hands two
/// *overlapping* MCP requests two genuinely different DI scopes.
/// </summary>
/// <remarks>
/// Uses <see cref="WebApplicationFactory{TEntryPoint}"/> (in-process TestServer, no real socket) rather
/// than a subprocess specifically because a real subprocess can't share an EF Core InMemory database with
/// this test process — InMemory doesn't cross process boundaries — and no real Postgres is available in
/// this environment (same ceiling as everywhere else in this project). The property under test — per-request
/// DI scope isolation inside Kestrel's request pipeline — is exercised identically by an in-process
/// TestServer as by a real socket, so this isn't a compromise on what's being verified.
/// </remarks>
public sealed class McpServerConcurrentIsolationTests
{
    private const string SigningKey = "webappfactory-test-signing-key-webappfactory-test";

    private static string MintToken(Guid userId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "FinanceApp.Web",
            audience: "FinanceApp.McpServer",
            claims: [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task ConcurrentRequestsFromDifferentUsers_ThroughRealAuthAndDiPipeline_NeverCrossContaminate()
    {
        var dbName = Guid.NewGuid().ToString();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Satisfies Program.cs's startup check; never actually connected to — Testing:InMemoryDatabaseName
            // makes Program.cs register the InMemory provider instead (see its comment, docs/spec.md §5).
            builder.UseSetting("ConnectionStrings:Finance", "Host=unused;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Mcp:SigningKey", SigningKey);
            builder.UseSetting("Testing:InMemoryDatabaseName", dbName);
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            await db.Database.EnsureCreatedAsync();
            var groceries = await db.Categories.FirstAsync(c => c.Name == "Groceries" && c.Kind == CategoryKind.Expense);

            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = userA,
                CategoryId = groceries.Id,
                Amount = 333m,
                OccurredOn = new DateOnly(2026, 8, 7),
                Source = TransactionSource.Manual,
                CreatedAtUtc = DateTime.UtcNow,
            });
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = userB,
                CategoryId = groceries.Id,
                Amount = 444m,
                OccurredOn = new DateOnly(2026, 8, 8),
                Source = TransactionSource.Manual,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        async Task<decimal> ReadTotalExpenseAsync(Guid userId)
        {
            var httpClient = factory.CreateClient();
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = httpClient.BaseAddress!,
                    Name = $"finance-mcp-test-{userId:N}",
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {MintToken(userId)}" },
                },
                httpClient,
                NullLoggerFactory.Instance,
                ownsHttpClient: true);

            await using var client = await McpClient.CreateAsync(transport);
            var result = await client.ReadResourceAsync("skill://monthly-summary/summary-2026-08");
            var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("totalExpense").GetDecimal();
        }

        // Sequential first, to attribute any failure clearly to a specific user before adding concurrency.
        Assert.Equal(333m, await ReadTotalExpenseAsync(userA));
        Assert.Equal(444m, await ReadTotalExpenseAsync(userB));

        // Then genuinely overlapping — this is the case that would actually catch a shared/leaked DI scope.
        var concurrent = await Task.WhenAll(ReadTotalExpenseAsync(userA), ReadTotalExpenseAsync(userB));
        Assert.Equal(333m, concurrent[0]);
        Assert.Equal(444m, concurrent[1]);
    }
}
