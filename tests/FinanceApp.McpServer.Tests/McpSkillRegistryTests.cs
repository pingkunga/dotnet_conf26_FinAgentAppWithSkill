using System.Text.Json;
using FinanceApp.McpServer;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer.Tests;

/// <summary>
/// <see cref="McpSkillRegistry"/> merges every registered <see cref="IMcpSkillResourceHandler"/> into one
/// <c>skill://index.json</c> and routes <c>resources/read</c> by <see cref="IMcpSkillResourceHandler.CanHandle"/>
/// — tested here against small fake handlers (no real DB/HTTP) so the aggregation logic itself is pinned
/// down independently of any one skill's own computation, which its own test file already covers.
/// </summary>
public sealed class McpSkillRegistryTests
{
    private sealed class FakeHandler(string name, string uri, string content) : IMcpSkillResourceHandler
    {
        public object IndexEntry => new { name, type = "skill-md", description = $"{name} description", url = uri };

        public IEnumerable<Resource> ListableResources => [new Resource { Uri = uri, Name = name, MimeType = "text/markdown" }];

        public bool CanHandle(string candidateUri) => candidateUri == uri;

        public ValueTask<ReadResourceResult> ReadResourceAsync(string candidateUri, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReadResourceResult
            {
                Contents = [new TextResourceContents { Uri = candidateUri, MimeType = "text/plain", Text = content }],
            });
    }

    [Fact]
    public async Task ListResourcesCoreAsync_IncludesTheIndexAndEveryHandlersListableResources()
    {
        var registry = new McpSkillRegistry([
            new FakeHandler("skill-a", "skill://skill-a/SKILL.md", "a"),
            new FakeHandler("skill-b", "skill://skill-b/SKILL.md", "b"),
        ]);

        var result = await registry.ListResourcesCoreAsync();

        Assert.Contains(result.Resources, r => r.Uri == "skill://index.json");
        Assert.Contains(result.Resources, r => r.Uri == "skill://skill-a/SKILL.md");
        Assert.Contains(result.Resources, r => r.Uri == "skill://skill-b/SKILL.md");
        Assert.Equal(3, result.Resources.Count);
    }

    [Fact]
    public async Task ReadResourceCoreAsync_Index_MergesEveryHandlersIndexEntry()
    {
        var registry = new McpSkillRegistry([
            new FakeHandler("skill-a", "skill://skill-a/SKILL.md", "a"),
            new FakeHandler("skill-b", "skill://skill-b/SKILL.md", "b"),
        ]);

        var result = await registry.ReadResourceCoreAsync("skill://index.json", CancellationToken.None);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
        using var doc = JsonDocument.Parse(text);
        var skills = doc.RootElement.GetProperty("skills");
        Assert.Equal(2, skills.GetArrayLength());
        Assert.Contains(skills.EnumerateArray(), s => s.GetProperty("name").GetString() == "skill-a");
        Assert.Contains(skills.EnumerateArray(), s => s.GetProperty("name").GetString() == "skill-b");
    }

    [Fact]
    public async Task ReadResourceCoreAsync_RoutesToTheHandlerThatCanHandleTheUri()
    {
        var registry = new McpSkillRegistry([
            new FakeHandler("skill-a", "skill://skill-a/SKILL.md", "content-a"),
            new FakeHandler("skill-b", "skill://skill-b/SKILL.md", "content-b"),
        ]);

        var result = await registry.ReadResourceCoreAsync("skill://skill-b/SKILL.md", CancellationToken.None);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
        Assert.Equal("content-b", text);
    }

    [Fact]
    public async Task ReadResourceCoreAsync_NoHandlerCanHandleTheUri_ThrowsMcpException()
    {
        var registry = new McpSkillRegistry([new FakeHandler("skill-a", "skill://skill-a/SKILL.md", "a")]);

        await Assert.ThrowsAsync<McpException>(() =>
            registry.ReadResourceCoreAsync("skill://something-else", CancellationToken.None).AsTask());
    }
}
