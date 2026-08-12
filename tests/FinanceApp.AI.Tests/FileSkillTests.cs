using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FinanceApp.AI.Tests;

/// <summary>
/// Real end-to-end round trip (docs/spec.md §8 bar: Docker/LLM-free) against the two real file-based
/// skills under the repo's <c>skills/</c> directory (docs/spec.md §4.3), copied into this test's output by
/// <c>FinanceApp.AI.Tests.csproj</c>'s Content item — the same mechanism <c>FinanceApp.Web.csproj</c> uses
/// at run time, so a broken frontmatter, a renamed reference/script, or a dropped Content item all fail
/// this test instead of only surfacing as a silent "not found" against a real LLM.
/// </summary>
/// <remarks>
/// Deliberately does **not** invoke <c>run_skill_script</c> against <c>savings-calculator</c> — that would
/// actually shell out to python3 via <see cref="FinanceApp.Skills.SubprocessScriptRunner"/>, which isn't
/// guaranteed available wherever these tests run (confirmed absent on this dev machine — see
/// docs/spec.md §4.3). <c>SubprocessScriptRunnerTests</c> in <c>FinanceApp.Skills.Tests</c> covers the
/// runner's own logic without spawning a real interpreter.
/// </remarks>
public sealed class FileSkillTests
{
    [Fact]
    public async Task LoadSkill_SavingsGoals_ListsBothReferencesAndNoScripts()
    {
        var content = await LoadSkillAsync("savings-goals");

        Assert.Contains("references/compound-interest.md", content);
        Assert.Contains("references/fifty-thirty-twenty.md", content);
        Assert.Contains("<available_scripts />", content);
    }

    [Fact]
    public async Task ReadSkillResource_SavingsGoals_ReturnsRealFileContent()
    {
        var content = await ReadSkillResourceAsync("savings-goals", "references/compound-interest.md");

        Assert.Contains("Compound interest, in plain language", content);
        Assert.Contains("savings-calculator", content); // the hand-off line at the end of the real file
    }

    [Fact]
    public async Task LoadSkill_SavingsCalculator_ListsTheScriptWithItsParametersSchema()
    {
        var content = await LoadSkillAsync("savings-calculator");

        Assert.Contains("scripts/project-savings.py", content);
        Assert.Contains("<available_resources />", content); // no references/ folder for this skill
        Assert.Contains("\"type\":\"array\"", content); // the generic array-of-strings parameters_schema
    }

    private static async Task<string> LoadSkillAsync(string skillName) =>
        await RunSingleToolCallAsync(AgentSkillsProvider.LoadSkillToolName, new Dictionary<string, object?>
        {
            ["skillName"] = skillName,
        });

    private static async Task<string> ReadSkillResourceAsync(string skillName, string resourceName)
    {
        // read_skill_resource needs load_skill to have already been called first in the same session in
        // real usage, but nothing in the framework actually enforces that ordering at the tool level — the
        // scripted client below still issues load_skill first anyway, to match how a real model behaves.
        var client = new SingleSkillScriptedClient(
            AgentSkillsProvider.ReadSkillResourceToolName,
            new Dictionary<string, object?> { ["skillName"] = skillName, ["resourceName"] = resourceName });
        return await RunAsync(client);
    }

    private static async Task<string> RunSingleToolCallAsync(string toolName, Dictionary<string, object?> arguments) =>
        await RunAsync(new SingleSkillScriptedClient(toolName, arguments));

    private static async Task<string> RunAsync(SingleSkillScriptedClient client)
    {
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
        var provider = new AgentSkillsProviderBuilder()
            .UseFileSkill(Path.Combine(skillsRoot, "savings-goals"), options: null,
                scriptRunner: (_, _, _, _, _) => throw new NotSupportedException("savings-goals has no scripts."))
            .UseFileSkill(Path.Combine(skillsRoot, "savings-calculator"), options: null,
                scriptRunner: (_, _, _, _, _) => throw new NotSupportedException("Not invoked by this test."))
            .UseOptions(o =>
            {
                o.DisableLoadSkillApproval = true;
                o.DisableReadSkillResourceApproval = true;
                o.DisableRunSkillScriptApproval = true;
            })
            .Build();

        var options = new ChatClientAgentOptions
        {
            AIContextProviders = [provider],
            ChatOptions = new ChatOptions { Instructions = "test" },
        };
        AIAgent agent = client.AsAIAgent(options);
        var session = await agent.CreateSessionAsync();

        await foreach (var _ in agent.RunStreamingAsync("go", session))
        {
            // Draining the stream is enough to make the tool call happen; the client itself captures the result.
        }

        return client.CapturedResult ?? throw new InvalidOperationException("Tool call result was never captured.");
    }

    /// <summary>
    /// Drives exactly the two-step "load_skill (or the requested tool directly) → done" flow needed to
    /// capture one tool's <see cref="FunctionResultContent"/>, mirroring the pattern established in
    /// <see cref="SkillActivityExtractorTests"/>'s scripted client.
    /// </summary>
    private sealed class SingleSkillScriptedClient(string toolName, Dictionary<string, object?> arguments) : IChatClient
    {
        public string? CapturedResult { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var priorCalls = messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.Name).ToList();

            if (!priorCalls.Contains(toolName))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", toolName, arguments)])
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                };
            }
            else
            {
                CapturedResult = messages
                    .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                    .Select(c => c.Result?.ToString())
                    .LastOrDefault(r => r is not null);

                yield return new ChatResponseUpdate(ChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop };
            }

            await Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test double only exercises the streaming path.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
