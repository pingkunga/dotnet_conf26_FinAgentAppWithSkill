using ModelContextProtocol.Client;

namespace FinanceApp.Web.Services;

/// <summary>
/// Connects to the (now standing, HTTP-hosted) <c>FinanceApp.McpServer</c> service on behalf of one chat
/// session (docs/spec.md §5, Step 1 of the HTTP-migration plan). Previously spawned a fresh subprocess per
/// chat session over stdio; the server is now a shared service reachable at <c>Mcp:BaseUrl</c>, so isolation
/// no longer comes from "which process this is" — it comes from the bearer token
/// <see cref="McpAccessTokenIssuer"/> mints for this exact <paramref name="userId"/> and the server's
/// per-request claims resolution (<c>HttpUserContextAccessor</c> on the McpServer side). <c>userId</c>
/// still never travels as an MCP request argument — it's baked into the token, not something the LLM
/// supplies (CLAUDE.md's firm "never trust an LLM-provided user-identifier-shaped value" rule).
/// </summary>
/// <remarks>
/// Graceful degradation unchanged in shape: any failure to connect is caught and logged, returning
/// <see langword="null"/> — <see cref="ChatSessionService"/> skips <c>.UseMcpSkills(...)</c> for that
/// session, chat still works with the other 3 skill sources (docs/spec.md §4.4). The failure mode is now
/// "server unreachable / token rejected" instead of "subprocess failed to spawn".
/// </remarks>
public sealed class McpServerLauncher(IConfiguration configuration, McpAccessTokenIssuer tokenIssuer, ILoggerFactory loggerFactory)
{
    private readonly ILogger<McpServerLauncher> _logger = loggerFactory.CreateLogger<McpServerLauncher>();

    public async Task<McpClient?> TryStartAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = configuration["Mcp:BaseUrl"]
                ?? throw new InvalidOperationException("Missing Mcp:BaseUrl configuration.");
            var token = tokenIssuer.IssueToken(userId);

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(baseUrl),
                Name = $"finance-mcp-{userId:N}",
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            });

            return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to connect to the monthly-summary MCP server; that skill will be unavailable for this chat session.");
            return null;
        }
    }
}
