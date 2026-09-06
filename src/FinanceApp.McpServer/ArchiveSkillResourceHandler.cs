using System.IO.Compression;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer;

/// <summary>
/// Serves one skill packaged as a zip archive (<c>McpSkillIndexEntry.Type == "archive"</c>) instead of a
/// bare <c>skill-md</c> resource — the archive-distribution path <c>Microsoft.Agents.AI.Mcp</c>'s client
/// (<c>ArchiveEntryLoader</c>) supports but this project never exercised until now: the
/// <c>AgentMcpSkillsSourceOptions.Archive*</c> limits <c>ChatSessionService</c> could configure were inert
/// with only <c>skill-md</c> entries in play, because no archive was ever downloaded to bound. Once this
/// handler exists, those limits become load-bearing for the first time (docs/spec.md §4.4).
/// </summary>
/// <remarks>
/// <strong>Only suitable for guidance/reference skills with no runnable scripts.</strong> Per
/// <c>ArchiveEntryLoader</c>'s own doc comment: "scripts bundled inside an archive are surfaced as
/// readable resources only; they are never discovered as executable scripts" — a skill shaped like
/// <c>savings-calculator</c> (has <c>scripts/*.py</c> a <c>SubprocessScriptRunner</c> actually executes)
/// would silently lose that capability if served this way. This handler is for skills shaped like the
/// file-based <c>savings-goals</c> skill instead: <c>SKILL.md</c> + <c>references/*.md</c>, nothing else.
/// </remarks>
/// <remarks>
/// One instance per archived skill — constructed directly with the values that differ per skill, not
/// resolved by concrete type via DI (see <c>Program.cs</c>'s
/// <c>AddScoped&lt;IMcpSkillResourceHandler&gt;(_ =&gt; new ArchiveSkillResourceHandler(...))</c>
/// registrations, one per skill). The zip itself is built once, lazily, from <paramref name="skillFolderPath"/>
/// and cached in a process-wide static dictionary keyed by that path — the source files are static, so
/// rebuilding the archive from disk on every request would be pure waste. Thread-safe: concurrent first
/// reads for the same skill block on one lock rather than racing to build duplicate zips.
/// </remarks>
public sealed class ArchiveSkillResourceHandler(
    string skillName,
    string skillFolderPath,
    string description,
    ILogger<ArchiveSkillResourceHandler> logger)
    : IMcpSkillResourceHandler
{
    private static readonly Dictionary<string, byte[]> ZipCache = [];
    private static readonly object ZipCacheLock = new();

    private string ArchiveUri => $"skill://{skillName}/archive.zip";

    public object IndexEntry => new
    {
        name = skillName,
        type = "archive",
        description,
        url = ArchiveUri,
    };

    // Archive-type entries have no separate listed SKILL.md resource — their whole content
    // (SKILL.md + references) lives inside the one archive the index entry's `url` already points at,
    // so there's nothing additional to advertise via resources/list.
    public IEnumerable<Resource> ListableResources => [];

    public bool CanHandle(string uri) => uri == ArchiveUri;

    public ValueTask<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    {
        if (uri != ArchiveUri)
        {
            throw new McpException($"Unknown resource: {uri}");
        }

        logger.LogInformation("MCP read_skill_resource: {Uri} (archive)", uri);

        var bytes = GetOrBuildZip();
        return ValueTask.FromResult(new ReadResourceResult
        {
            // The contents of the archive are returned as a base64-encoded string inside the Blob property.
            // This ensures that the binary data is safely transmitted over JSON without corruption.
            Contents = [new BlobResourceContents
            {
                Uri = uri,
                MimeType = "application/zip",
                Blob = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes)),
            }],
        });
    }

    private byte[] GetOrBuildZip()
    {
        lock (ZipCacheLock)
        {
            if (ZipCache.TryGetValue(skillFolderPath, out var cached))
            {
                return cached;
            }

            using var memoryStream = new MemoryStream();
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(skillFolderPath, "*", SearchOption.AllDirectories))
                {
                    var entryName = Path.GetRelativePath(skillFolderPath, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, entryName);
                }
            }

            var bytes = memoryStream.ToArray();
            ZipCache[skillFolderPath] = bytes;
            return bytes;
        }
    }
}
