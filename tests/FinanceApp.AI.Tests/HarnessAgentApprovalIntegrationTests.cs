using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FinanceApp.AI.Tests;

/// <summary>
/// The permanent test that <see cref="SkillApprovalPolicyTests"/>/<see cref="SkillActionClassifierTests"/>
/// cannot be, by construction: those two call <see cref="SkillApprovalPolicy.IsAutoApproved"/>/
/// <see cref="SkillActionClassifier.Classify"/> directly with hand-built <see cref="FunctionCallContent"/>,
/// so they'd stay green even if switching <see cref="AgentFactory"/> to <c>AsHarnessAgent</c> silently broke
/// the *production* wiring (different tool-call names/shape, approval middleware not actually applied,
/// etc.) — the same "structurally incapable of catching the real risk" trap this project's
/// <c>MonthlySummaryResourceHandlersTests</c> hit once before with hand-constructed handlers (see the plan
/// history in <c>docs/spec.md</c>). This test instead builds the agent through the real
/// <see cref="AgentFactory"/> — exactly how <c>ChatSessionService</c> does in production — and proves the
/// approval pipeline actually gates the right calls end to end. Promoted from a throwaway spike
/// (2026-08-16, scratchpad, deleted after) that confirmed the same 4 things this test now asserts
/// permanently.
/// </summary>
public class HarnessAgentApprovalIntegrationTests
{
    [Fact]
    public async Task GatedScript_StopsOnApprovalRequest_WhileUnclassifiedScriptOnSameSkillRunsSilently()
    {
        var chatClient = new ScriptedChatClient();
        var skillsProvider = new AgentSkillsProviderBuilder()
            .UseSkill(new FakeBudgetingSkill())
            .UseOptions(o =>
            {
                o.DisableLoadSkillApproval = true;
                o.DisableReadSkillResourceApproval = true;
                o.DisableRunSkillScriptApproval = false;
            })
            .Build();

        // Real production policy — autoApproveWrites: false gates "add_transaction" (classified Write),
        // but "check_budget_status" (unclassified — None) must still run without ever prompting.
        var toolApprovalOptions = new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [SkillApprovalPolicy.BuildAutoApprovalRule(autoApproveWrites: false, autoApproveExecuteScript: true)],
        };

        IAgentFactory agentFactory = new AgentFactory(chatClient);
        var agent = agentFactory.CreateAgent(skillsProvider, "test", toolApprovalOptions);
        var session = await agent.CreateSessionAsync();

        // --- Unclassified script: must complete with no ToolApprovalRequestContent anywhere in the stream. ---
        chatClient.Enqueue(
            LoadSkill("budgeting"),
            RunScript("budgeting", "check_budget_status"),
            FinalText("status: ok"));

        var sawApprovalRequestForRead = false;
        string? readFinalText = null;
        await foreach (var update in agent.RunStreamingAsync("check my budget", session))
        {
            if (update.Contents.OfType<ToolApprovalRequestContent>().Any())
            {
                sawApprovalRequestForRead = true;
            }
            readFinalText += update.Text;
        }

        Assert.False(sawApprovalRequestForRead);
        Assert.Equal("status: ok", readFinalText);

        // --- Classified-Write script: must stop on a ToolApprovalRequestContent naming the real skill/script. ---
        chatClient.Enqueue(RunScript("budgeting", "add_transaction"), FinalText("added"));

        ToolApprovalRequestContent? pending = null;
        await foreach (var update in agent.RunStreamingAsync("add a $10 transaction", session))
        {
            pending = update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault() ?? pending;
        }

        Assert.NotNull(pending);
        Assert.Equal("budgeting: run add_transaction", SkillActivityExtractor.DescribeApprovalRequest(pending!));

        // --- Resuming with an approval response completes the run (ChatSessionService.ResumeWithApprovalAsync's shape). ---
        var response = pending!.CreateResponse(approved: true, reason: null);
        string? resumedFinalText = null;
        await foreach (var update in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, [response]), session))
        {
            resumedFinalText += update.Text;
        }

        Assert.Equal("added", resumedFinalText);
    }

    private static ChatResponseUpdate LoadSkill(string skillName) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString(), AgentSkillsProvider.LoadSkillToolName,
            new Dictionary<string, object?> { ["skillName"] = skillName })])
        { FinishReason = ChatFinishReason.ToolCalls };

    private static ChatResponseUpdate RunScript(string skillName, string scriptName) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString(), AgentSkillsProvider.RunSkillScriptToolName,
            new Dictionary<string, object?>
            {
                ["skillName"] = skillName,
                ["scriptName"] = scriptName,
                ["arguments"] = new Dictionary<string, object?>(),
            })])
        { FinishReason = ChatFinishReason.ToolCalls };

    private static ChatResponseUpdate FinalText(string text) =>
        new(ChatRole.Assistant, text) { FinishReason = ChatFinishReason.Stop };

    /// <summary>
    /// A fake <see cref="AgentClassSkill{T}"/> literally named "budgeting" with the two real script names
    /// this test needs classified differently by <see cref="SkillActionClassifier"/> — deliberately not the
    /// production <c>BudgetSkill</c> (which needs a DB), since only the tool-call names/shape matter here,
    /// not real budget math.
    /// </summary>
    private sealed class FakeBudgetingSkill : AgentClassSkill<FakeBudgetingSkill>
    {
        public FakeBudgetingSkill() : base(argumentMarshaler: null) { }
        public override AgentSkillFrontmatter Frontmatter { get; } = new("budgeting", "Fake budgeting skill for approval testing.", null);
        protected override string Instructions => "Use add_transaction or check_budget_status.";

        [AgentSkillScript("add_transaction")]
        public Task<string> AddTransactionAsync() => Task.FromResult("added");

        [AgentSkillScript("check_budget_status")]
        public Task<string> CheckBudgetStatusAsync() => Task.FromResult("status: ok");
    }

    /// <summary>
    /// A fake model driven by an explicit queue of scripted responses (one per model turn) — simpler than
    /// inspecting message history, since this test drives multiple distinct scenarios sequentially against
    /// one long-lived agent/session (mirrors the throwaway spike this test was promoted from).
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatResponseUpdate> _queue = new();

        public void Enqueue(params ChatResponseUpdate[] updates)
        {
            foreach (var u in updates)
            {
                _queue.Enqueue(u);
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_queue.Count == 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "no more script") { FinishReason = ChatFinishReason.Stop };
                yield break;
            }

            yield return _queue.Dequeue();
            await Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test double only exercises the streaming path.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
