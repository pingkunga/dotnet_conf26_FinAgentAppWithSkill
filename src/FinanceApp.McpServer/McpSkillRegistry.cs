using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FinanceApp.McpServer;

/// <summary>
/// Merges every registered <see cref="IMcpSkillResourceHandler"/> into the single <c>resources/list</c>/
/// <c>resources/read</c> handler pair <c>Program.cs</c> wires into <c>AddMcpServer()</c> — the aggregation
/// point that makes adding a third or fourth skill to this server a matter of writing one more handler
/// class + one more DI registration, not editing this file or <c>Program.cs</c>'s wiring again (docs/spec.md
/// §4.4). Registered scoped, mirroring every per-skill handler it wraps (docs/spec.md §5's "fresh
/// instance/fresh <c>FinanceDbContext</c> per HTTP request" rule) — a singleton here would hold scoped
/// handler instances past their request's lifetime.
/// </summary>
public sealed class McpSkillRegistry(IEnumerable<IMcpSkillResourceHandler> handlers)
{
    public const string IndexUri = "skill://index.json";

    public ValueTask<ListResourcesResult> ListResourcesAsync(
        RequestContext<ListResourcesRequestParams> _, CancellationToken __) =>
        ListResourcesCoreAsync();

    /// <summary>URI-based core, split out from the <c>RequestContext</c>-taking overload above so it's
    /// directly unit-testable without constructing a real <c>RequestContext&lt;T&gt;</c> — same pattern
    /// <see cref="MonthlySummaryResourceHandlers.ListResourcesCoreAsync"/> already used.</summary>
    public ValueTask<ListResourcesResult> ListResourcesCoreAsync() =>
        ValueTask.FromResult(new ListResourcesResult
        {
            Resources =
            [
                new Resource { Uri = IndexUri, Name = "skill-index", MimeType = "application/json" },
                .. handlers.SelectMany(h => h.ListableResources),
            ],
        });

    public ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var uri = context.Params?.Uri
            ?? throw new McpException("Missing resource uri.");
        return ReadResourceCoreAsync(uri, cancellationToken);
    }

    /// <summary>URI-based core — see <see cref="ListResourcesCoreAsync"/>'s remark.</summary>
    public ValueTask<ReadResourceResult> ReadResourceCoreAsync(string uri, CancellationToken cancellationToken)
    {
        if (uri == IndexUri)
        {
            return ValueTask.FromResult(BuildIndexResult(uri));
        }

        var handler = handlers.FirstOrDefault(h => h.CanHandle(uri))
            ?? throw new McpException($"Unknown resource: {uri}");
        return handler.ReadResourceAsync(uri, cancellationToken);
    }

    private ReadResourceResult BuildIndexResult(string uri)
    {
        var index = new { skills = handlers.Select(h => h.IndexEntry).ToArray() };
        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = JsonSerializer.Serialize(index) }],
        };
    }
}
