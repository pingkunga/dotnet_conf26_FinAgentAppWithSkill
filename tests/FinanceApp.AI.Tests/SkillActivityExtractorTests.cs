using System.Runtime.CompilerServices;
using FinanceApp.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FinanceApp.AI.Tests;

/// <summary>
/// Docker/LLM-free (docs/spec.md §8 bar): drives a real <see cref="AgentClassSkill{T}"/> through a real
/// <c>AsAIAgent</c> pipeline with a hand-scripted fake <see cref="IChatClient"/> standing in for the model —
/// the same shape as the scratchpad spike that discovered the tool-approval-default gap (see
/// docs/spec.md §7). Proves <see cref="SkillActivityExtractor"/> correctly reads the real stream, not a
/// guessed shape.
/// </summary>

public class SkillActivityExtractorTests
{
    [Fact]
    public async Task Extract_PullsLoadSkillAndRunSkillScriptEntries_FromARealAgentRun()
    {
        var chatClient = new ScriptedToolCallingChatClient();
        var skillsProvider = new AgentSkillsProviderBuilder()
            .UseSkill(new EchoSkill())
            .UseOptions(o =>
            {
                o.DisableLoadSkillApproval = true;
                o.DisableReadSkillResourceApproval = true;
                o.DisableRunSkillScriptApproval = true;
            })
            .Build();

        var options = new ChatClientAgentOptions
        {
            AIContextProviders = [skillsProvider],
            ChatOptions = new ChatOptions { Instructions = "test" },
        };
        AIAgent agent = chatClient.AsAIAgent(options);
        var session = await agent.CreateSessionAsync();

        var entries = new List<SkillActivityEntry>();
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("please echo hi", session))
        {
            entries.AddRange(SkillActivityExtractor.Extract(update));
            finalText += update.Text;
        }

        Assert.Equal("All done.", finalText);
        Assert.Collection(entries,
            e =>
            {
                Assert.Equal("load_skill", e.ToolName);
                Assert.Equal("echo", e.SkillName);
                Assert.Null(e.Detail);
            },
            e =>
            {
                Assert.Equal("run_skill_script", e.ToolName);
                Assert.Equal("echo", e.SkillName);
                Assert.Equal("run_the_echo", e.Detail);
            });
    }

    [Fact]
    public void Extract_ReadsJsonElementArguments()
    {
        // A real provider delivers tool-call arguments as parsed JSON (JsonElement), not plain CLR
        // strings like the scripted-client test above uses — this closes that encoding gap.
        using var doc = System.Text.Json.JsonDocument.Parse("""{"skillName":"budgeting"}""");
        var call = new FunctionCallContent("call-1", AgentSkillsProvider.LoadSkillToolName,
            new Dictionary<string, object?> { ["skillName"] = doc.RootElement.GetProperty("skillName") });
        var update = new AgentResponseUpdate(ChatRole.Assistant, [call]);

        var entry = Assert.Single(SkillActivityExtractor.Extract(update));

        Assert.Equal("load_skill", entry.ToolName);
        Assert.Equal("budgeting", entry.SkillName);
    }

    [Fact]
    public void Extract_IgnoresNonSkillFunctionCalls()
    {
        var update = new AgentResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "some_unrelated_tool", new Dictionary<string, object?>())]);

        Assert.Empty(SkillActivityExtractor.Extract(update));
    }

    private sealed class EchoSkill : AgentClassSkill<EchoSkill>
    {
        public EchoSkill() : base(argumentMarshaler: null) { }

        public override AgentSkillFrontmatter Frontmatter { get; } = new("echo", "Echoes text back.", null);

        protected override string Instructions => "Use run_the_echo to echo text.";

        [AgentSkillScript("run_the_echo")]
        public Task<string> EchoAsync(string text) => Task.FromResult($"echo: {text}");
    }

    /// <summary>
    /// Fakes a model that always drives the canonical 3-step skill flow: load_skill → run_skill_script →
    /// final text — by inspecting which FunctionCallContent names have already appeared in the message
    /// history (the function-invoking middleware re-calls this after each tool result, same as a real
    /// provider would).
    /// </summary>
    private sealed class ScriptedToolCallingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var priorCalls = messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Select(c => c.Name)
                .ToList();

            if (!priorCalls.Contains(AgentSkillsProvider.LoadSkillToolName))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", AgentSkillsProvider.LoadSkillToolName,
                        new Dictionary<string, object?> { ["skillName"] = "echo" })])
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                };
            }
            else if (!priorCalls.Contains(AgentSkillsProvider.RunSkillScriptToolName))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-2", AgentSkillsProvider.RunSkillScriptToolName,
                        new Dictionary<string, object?>
                        {
                            ["skillName"] = "echo",
                            ["scriptName"] = "run_the_echo",
                            ["arguments"] = new Dictionary<string, object?> { ["text"] = "hi" },
                        })])
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                };
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "All done.")
                {
                    FinishReason = ChatFinishReason.Stop,
                };
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
