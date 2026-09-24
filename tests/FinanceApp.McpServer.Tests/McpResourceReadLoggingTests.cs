using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FinanceApp.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;

namespace FinanceApp.McpServer.Tests;

/// <summary>
/// <see cref="McpResourceReadLogging"/> through the real Program.cs wiring (JwtBearer + request filter +
/// scoped DI), same <see cref="WebApplicationFactory{TEntryPoint}"/> setup as
/// <see cref="McpServerConcurrentIsolationTests"/>: each <c>resources/read</c> must be logged with the uri
/// and the user from the bearer token, text previewed and archive blobs reported by size only.
/// </summary>
public sealed class McpResourceReadLoggingTests
{
    private const string SigningKey = "webappfactory-test-signing-key-webappfactory-test";

    [Fact]
    public async Task ResourceRead_IsLoggedWithUriUserAndResult()
    {
        var userId = Guid.NewGuid();
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFactory(logs, includePayloads: true);
        await EnsureDatabaseAsync(factory);

        await using var client = await ConnectAsync(factory, userId);
        await client.ReadResourceAsync("skill://monthly-summary/summary-2026-08");
        await client.ReadResourceAsync("skill://emergency-fund/archive.zip");

        var summary = Assert.Single(logs.Messages, m => m.Contains("skill://monthly-summary/summary-2026-08"));
        Assert.Contains($"user={userId}", summary);
        Assert.Contains("totalExpense", summary);

        var archive = Assert.Single(logs.Messages, m => m.Contains("skill://emergency-fund/archive.zip") && m.Contains("resources/read"));
        Assert.Contains("(blob", archive);
        Assert.Contains("bytes)", archive);
    }

    [Fact]
    public async Task IncludePayloadsFalse_OmitsResult()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFactory(logs, includePayloads: false);
        await EnsureDatabaseAsync(factory);

        await using var client = await ConnectAsync(factory, Guid.NewGuid());
        await client.ReadResourceAsync("skill://monthly-summary/summary-2026-08");

        var entry = Assert.Single(logs.Messages, m => m.Contains("skill://monthly-summary/summary-2026-08") && m.Contains("resources/read"));
        Assert.Contains("(omitted)", entry);
        Assert.DoesNotContain("totalExpense", entry);
    }

    private static WebApplicationFactory<Program> CreateFactory(CapturingLoggerProvider logs, bool includePayloads) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Finance", "Host=unused;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Mcp:SigningKey", SigningKey);
            builder.UseSetting("Testing:InMemoryDatabaseName", Guid.NewGuid().ToString());
            builder.UseSetting("SkillCallLogging:IncludePayloads", includePayloads.ToString());
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));
        });

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Database.EnsureCreatedAsync();
    }

    private static Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        var httpClient = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = httpClient.BaseAddress!,
                Name = "finance-mcp-logging-test",
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {MintToken(userId)}" },
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        return McpClient.CreateAsync(transport);
    }

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

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(McpResourceReadLogging).FullName ? new CapturingLogger(_messages) : NullLogger.Instance;

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
