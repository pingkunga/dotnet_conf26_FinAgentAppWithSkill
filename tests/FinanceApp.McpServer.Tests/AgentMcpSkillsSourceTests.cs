using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FinanceApp.Core;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;

namespace FinanceApp.McpServer.Tests;

public sealed class AgentMcpSkillsSourceTests
{
    private const string SigningKey = "webappfactory-test-signing-key-webappfactory-test";

    [Fact]
    public async Task ArchiveSkills_AreExtractedIntoConfiguredDirectory_NotCurrentDirectory()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Finance", "Host=unused;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Mcp:SigningKey", SigningKey);
            builder.UseSetting("Testing:InMemoryDatabaseName", Guid.NewGuid().ToString());
        });
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Database.EnsureCreatedAsync();
        }

        var extractionDirectory = Path.Combine(Path.GetTempPath(), "mcp-skills-source-test-" + Guid.NewGuid().ToString("N"));
        var currentDirectoryBefore = Directory.GetDirectories(Directory.GetCurrentDirectory()).ToHashSet();

        try
        {
            await using var client = await ConnectAsync(factory, Guid.NewGuid());
            var skillsProvider = new AgentSkillsProviderBuilder()
                .UseMcpSkills(client, new AgentMcpSkillsSourceOptions { ArchiveSkillsDirectory = extractionDirectory })
                .Build();
            var model = new RecordingChatClient();
            var agent = model.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [skillsProvider] });

            await agent.RunAsync("hi", await agent.CreateSessionAsync());

            // The provider advertises every discovered skill to the model — all three kinds must be there.
            Assert.Contains("emergency-fund", model.LastRequest);
            Assert.Contains("debt-payoff-strategies", model.LastRequest);
            Assert.Contains("monthly-summary", model.LastRequest);
            Assert.True(File.Exists(Path.Combine(extractionDirectory, "emergency-fund", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(extractionDirectory, "debt-payoff-strategies", "SKILL.md")));

            var newInCurrentDirectory = Directory.GetDirectories(Directory.GetCurrentDirectory()).Except(currentDirectoryBefore);
            Assert.Empty(newInCurrentDirectory);
        }
        finally
        {
            if (Directory.Exists(extractionDirectory))
            {
                Directory.Delete(extractionDirectory, recursive: true);
            }
        }
    }

    private static Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        var httpClient = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = httpClient.BaseAddress!,
                Name = "finance-mcp-skills-source-test",
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

    /// <summary>Fake model that records what it was sent (instructions + messages) and answers "ok".</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public string LastRequest { get; private set; } = "";

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test only exercises the non-streaming path.");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastRequest = string.Join(Environment.NewLine, [options?.Instructions, .. messages.Select(m => m.Text)]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
