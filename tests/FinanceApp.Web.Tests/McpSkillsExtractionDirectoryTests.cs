using FinanceApp.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinanceApp.Web.Tests;

public sealed class McpSkillsExtractionDirectoryTests
{
    [Fact]
    public void CreateForSession_CreatesADistinctDirectoryUnderBase_PerCall()
    {
        var first = McpSkillsExtractionDirectory.CreateForSession();
        var second = McpSkillsExtractionDirectory.CreateForSession();

        try
        {
            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
            Assert.Equal(McpSkillsExtractionDirectory.BaseDirectory, Path.GetDirectoryName(first));
        }
        finally
        {
            McpSkillsExtractionDirectory.TryDelete(first, NullLogger.Instance);
            McpSkillsExtractionDirectory.TryDelete(second, NullLogger.Instance);
        }
    }

    [Fact]
    public void TryDelete_RemovesExtractedContent_AndIgnoresMissingOrNullPaths()
    {
        var path = McpSkillsExtractionDirectory.CreateForSession();
        Directory.CreateDirectory(Path.Combine(path, "emergency-fund", "references"));
        File.WriteAllText(Path.Combine(path, "emergency-fund", "SKILL.md"), "x");

        McpSkillsExtractionDirectory.TryDelete(path, NullLogger.Instance);

        Assert.False(Directory.Exists(path));
        McpSkillsExtractionDirectory.TryDelete(path, NullLogger.Instance);
        McpSkillsExtractionDirectory.TryDelete(null, NullLogger.Instance);
    }

    [Fact]
    public void CleanupStale_RemovesLeftoverSessionDirectories()
    {
        // Own temp root, not the real BaseDirectory — a running dev app's live sessions may be using that.
        var root = Path.Combine(Path.GetTempPath(), "mcp-skills-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var leftover = Path.Combine(root, Guid.NewGuid().ToString("N"), "emergency-fund");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "SKILL.md"), "x");

        try
        {
            McpSkillsExtractionDirectory.CleanupStale(NullLogger.Instance, root);

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
