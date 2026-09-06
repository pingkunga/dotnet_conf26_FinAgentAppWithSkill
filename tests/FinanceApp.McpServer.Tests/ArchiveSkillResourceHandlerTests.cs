using System.IO.Compression;
using System.Text.Json;
using FinanceApp.McpServer;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer.Tests;

/// <summary>
/// <see cref="ArchiveSkillResourceHandler"/> serves a skill as a <c>type: "archive"</c> index entry — the
/// distribution path <c>Microsoft.Agents.AI.Mcp</c>'s client supports (zip/tar, extracted to local disk
/// and re-discovered as a file-skill) but this project never exercised until <c>emergency-fund</c>/
/// <c>debt-payoff-strategies</c> (docs/spec.md §4.4). Tested here by building a real zip from a real temp
/// directory and reading it back — no MCP transport needed to prove the archive itself is well-formed.
/// </summary>
public sealed class ArchiveSkillResourceHandlerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mcp-archive-skill-tests-" + Guid.NewGuid());

    public ArchiveSkillResourceHandlerTests()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "references"));
        File.WriteAllText(Path.Combine(_tempDir, "SKILL.md"), "---\nname: test-skill\n---\n# Test Skill\n");
        File.WriteAllText(Path.Combine(_tempDir, "references", "extra.md"), "# Extra reference\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private ArchiveSkillResourceHandler CreateHandler() =>
        new("test-skill", _tempDir, "A test skill.", NullLogger<ArchiveSkillResourceHandler>.Instance);

    [Fact]
    public void IndexEntry_HasArchiveTypeAndAUrlPointingAtTheArchiveResource()
    {
        var handler = CreateHandler();

        var entry = JsonSerializer.SerializeToElement(handler.IndexEntry);

        Assert.Equal("test-skill", entry.GetProperty("name").GetString());
        Assert.Equal("archive", entry.GetProperty("type").GetString());
        Assert.Equal("A test skill.", entry.GetProperty("description").GetString());
        Assert.Equal("skill://test-skill/archive.zip", entry.GetProperty("url").GetString());
    }

    [Fact]
    public void ListableResources_IsEmpty()
    {
        // Archive-type entries have no separate listed SKILL.md — everything lives inside the one archive
        // the index entry's url already points at (see the handler's own remarks).
        var handler = CreateHandler();

        Assert.Empty(handler.ListableResources);
    }

    [Fact]
    public void CanHandle_OnlyMatchesItsOwnArchiveUri()
    {
        var handler = CreateHandler();

        Assert.True(handler.CanHandle("skill://test-skill/archive.zip"));
        Assert.False(handler.CanHandle("skill://other-skill/archive.zip"));
        Assert.False(handler.CanHandle("skill://index.json"));
    }

    [Fact]
    public async Task ReadResourceAsync_ReturnsAValidZipContainingEveryFileInTheSkillFolder()
    {
        var handler = CreateHandler();

        var result = await handler.ReadResourceAsync("skill://test-skill/archive.zip", CancellationToken.None);

        var blob = Assert.IsType<BlobResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("application/zip", blob.MimeType);

        // Blob holds the UTF8 bytes of the base64-encoded zip, not the raw zip bytes — see the handler's
        // own remarks on why (a write-side quirk in this preview SDK version).
        var zipBytes = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(blob.Blob.ToArray()));
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(["SKILL.md", "references/extra.md"], entryNames);

        using var reader = new StreamReader(archive.GetEntry("SKILL.md")!.Open());
        Assert.Contains("name: test-skill", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ReadResourceAsync_UnknownUri_ThrowsMcpException()
    {
        var handler = CreateHandler();

        await Assert.ThrowsAsync<McpException>(() =>
            handler.ReadResourceAsync("skill://test-skill/not-the-archive", CancellationToken.None).AsTask());
    }
}
