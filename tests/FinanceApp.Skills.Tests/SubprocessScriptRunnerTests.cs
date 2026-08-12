using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// Unit tests for <see cref="SubprocessScriptRunner"/>'s argument-handling and error paths — deliberately
/// **not** invoking python3 (Docker/LLM-free bar, docs/spec.md §8; a real end-to-end invocation would need
/// python3 available in whatever environment runs the tests, which isn't guaranteed here — see
/// docs/spec.md §4.3's dev-machine note). Every case below short-circuits before <c>Process.Start</c>.
/// </summary>
/// <remarks>
/// <see cref="AgentFileSkill"/>/<see cref="AgentFileSkillScript"/> have no public constructor — the only way
/// to get real instances is through the framework's own discovery pipeline
/// (<see cref="AgentFileSkillsSource"/>, confirmed public via a spike), so these tests discover the real
/// `skills/savings-calculator` script (copied into the test output by
/// <c>FinanceApp.Skills.Tests.csproj</c>'s Content item) rather than faking objects.
/// </remarks>
public sealed class SubprocessScriptRunnerTests
{
    [Fact]
    public async Task RunAsync_ThrowsInvalidOperationException_WhenArgumentsAreNotAnArray()
    {
        var script = await DiscoverProjectSavingsScriptAsync();
        using var badArgs = JsonDocument.Parse("""{"current_amount": "1000"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubprocessScriptRunner.RunAsync(script.Skill, script.Script, badArgs.RootElement, null, CancellationToken.None));

        Assert.Contains("JSON array", ex.Message);
    }

    [Fact]
    public async Task RunAsync_ThrowsInvalidOperationException_WhenArrayElementIsNotAString()
    {
        var script = await DiscoverProjectSavingsScriptAsync();
        using var badArgs = JsonDocument.Parse("""[1000, 200, 5, 12]""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubprocessScriptRunner.RunAsync(script.Skill, script.Script, badArgs.RootElement, null, CancellationToken.None));

        Assert.Contains("string CLI arguments", ex.Message);
    }

    [Fact]
    public async Task RunAsync_ReturnsErrorString_WhenScriptFileIsMissing()
    {
        // A throwaway skill folder, not the real skills/savings-calculator — the script file is deleted
        // out from under the discovered AgentFileSkillScript before invocation, to exercise the
        // File.Exists guard without touching real repo content.
        //
        // The frontmatter `name` must equal the containing folder's own name, or discovery silently
        // returns zero skills (no exception, no error) — found via a spike, undocumented anywhere. Hence
        // "vanish-skill" is both the subfolder name and the frontmatter name below, not a random GUID.
        var tempSkillsRoot = Path.Combine(Path.GetTempPath(), "subprocess-runner-test-" + Guid.NewGuid(), "vanish-skill");
        var scriptsDir = Path.Combine(tempSkillsRoot, "scripts");
        Directory.CreateDirectory(scriptsDir);
        await File.WriteAllTextAsync(Path.Combine(tempSkillsRoot, "SKILL.md"),
            "---\nname: vanish-skill\ndescription: temp\n---\nTemp skill for a test.");
        var scriptPath = Path.Combine(scriptsDir, "vanish.py");
        await File.WriteAllTextAsync(scriptPath, "print('hi')\n");

        try
        {
            var (skill, script) = await DiscoverScriptAsync(tempSkillsRoot, "scripts/vanish.py");
            File.Delete(scriptPath);

            var result = await SubprocessScriptRunner.RunAsync(skill, script, null, null, CancellationToken.None);

            Assert.Contains("not found", Assert.IsType<string>(result));
        }
        finally
        {
            // tempSkillsRoot's parent is the GUID-named container created above — delete that, not just
            // the "vanish-skill" leaf, so nothing is left behind in the OS temp directory.
            Directory.Delete(Path.GetDirectoryName(tempSkillsRoot)!, recursive: true);
        }
    }

    private static async Task<(AgentFileSkill Skill, AgentFileSkillScript Script)> DiscoverProjectSavingsScriptAsync()
    {
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills", "savings-calculator");
        return await DiscoverScriptAsync(skillsRoot, "scripts/project-savings.py");
    }

    private static async Task<(AgentFileSkill Skill, AgentFileSkillScript Script)> DiscoverScriptAsync(
        string skillPath, string scriptName)
    {
        // scriptRunner is never actually invoked by GetSkillsAsync/GetScriptAsync — discovery alone doesn't
        // run anything — so a stub that would throw if called is fine here.
        var source = new AgentFileSkillsSource(
            skillPath,
            scriptRunner: (_, _, _, _, _) => throw new InvalidOperationException("Not expected to run during discovery."),
            options: null,
            loggerFactory: null);

        // AgentSkillsSourceContext requires a non-null AIAgent even though a file-based source never uses
        // it — any real AIAgent instance works, it's just never invoked.
        var dummyAgent = new UnusedChatClient().AsAIAgent(new ChatClientAgentOptions());
        var context = new AgentSkillsSourceContext(dummyAgent, session: null);

        var skills = await source.GetSkillsAsync(context, CancellationToken.None);
        var skill = Assert.IsType<AgentFileSkill>(Assert.Single(skills));
        var script = Assert.IsType<AgentFileSkillScript>(await skill.GetScriptAsync(scriptName, CancellationToken.None));
        return (skill, script);
    }

    private sealed class UnusedChatClient : IChatClient
    {
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Never called — only used to satisfy AgentSkillsSourceContext's non-null AIAgent.");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Never called — only used to satisfy AgentSkillsSourceContext's non-null AIAgent.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
