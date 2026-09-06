using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer;

/// <summary>
/// One skill's contribution to <c>FinanceApp.McpServer</c>'s shared <c>skill://index.json</c> and its own
/// URI-prefixed resource reads (docs/spec.md §4.4). Implemented once per skill hosted by this server
/// (<see cref="MonthlySummaryResourceHandlers"/>, <see cref="GoalsProgressResourceHandlers"/>,
/// <see cref="ArchiveSkillResourceHandler"/>) and aggregated by <see cref="McpSkillRegistry"/>, so
/// <c>Program.cs</c> registers exactly one <c>resources/list</c>/<c>resources/read</c> handler pair
/// regardless of how many skills this server hosts — added when a second skill (goals-progress) needed
/// a home and the original single-class-hardcodes-everything shape (see git history on
/// <c>MonthlySummaryResourceHandlers</c> before this file existed) had no way to register a second one
/// without editing that class's own index array and read-dispatch switch.
/// </summary>
public interface IMcpSkillResourceHandler
{
    /// <summary>
    /// This skill's <c>skill://index.json</c> entry (an anonymous object matching the
    /// <c>{ name, type, description, url, digest? }</c> shape <c>Microsoft.Agents.AI.Mcp</c> expects,
    /// docs/spec.md §4.4) — merged with every other handler's entry by <see cref="McpSkillRegistry"/> into
    /// the one shared index. <c>type</c> is <c>"skill-md"</c> for a plain-text/markdown skill or
    /// <c>"archive"</c> for a zip/tar-packaged one (see <see cref="ArchiveSkillResourceHandler"/>) — both
    /// are valid per the Agent Skills MCP binding's schema.
    /// </summary>
    object IndexEntry { get; }

    /// <summary>
    /// Resources this skill advertises via <c>resources/list</c> beyond the shared index itself — its own
    /// <c>SKILL.md</c> (or archive) resource at minimum. Deliberately excludes parametrized/live resources
    /// (e.g. a specific month or "current") that are read on demand without being listed — the same
    /// convention <c>monthly-summary</c> already used before this interface existed.
    /// </summary>
    IEnumerable<Resource> ListableResources { get; }

    /// <summary>
    /// True when this handler owns <paramref name="uri"/> — checked by <see cref="McpSkillRegistry"/> in
    /// handler-registration order, first match wins.
    /// </summary>
    bool CanHandle(string uri);

    /// <summary>Reads one resource this skill owns. Only ever called when <see cref="CanHandle"/> is true.</summary>
    ValueTask<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken);
}
