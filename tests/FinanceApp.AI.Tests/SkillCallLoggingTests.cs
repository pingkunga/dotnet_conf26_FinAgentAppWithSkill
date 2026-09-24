using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FinanceApp.AI.Tests;

/// <summary>
/// Drives <see cref="SkillCallLogging"/> through the real production composition — an agent built by
/// <see cref="AgentFactory"/> (HarnessAgent + tool approval), then wrapped exactly the way
/// <c>ChatSessionService</c> wraps it — against a scripted fake model. Proves the function-invocation
/// middleware actually sees skill-tool calls through HarnessAgent, and that wrapping doesn't break the
/// approval/resume flow <see cref="HarnessAgentApprovalIntegrationTests"/> covers.
/// </summary>
public class SkillCallLoggingTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task RunSkillScript_LogsSkillScriptArgsResultAndUser()
    {
        var (agent, chatClient, logger) = Build(new SkillCallLoggingOptions());
        var session = await agent.CreateSessionAsync();

        chatClient.Enqueue(
            LoadSkill("budgeting"),
            RunScript("budgeting", "check_budget_status", new Dictionary<string, object?> { ["category"] = "Food" }),
            FinalText("done"));
        await DrainAsync(agent.RunStreamingAsync("check my budget", session));

        var loadEntry = Assert.Single(logger.Entries, e => e.Message.Contains("load_skill"));
        Assert.Contains("budgeting", loadEntry.Message);

        var scriptEntry = Assert.Single(logger.Entries, e => e.Message.Contains(AgentSkillsProvider.RunSkillScriptToolName));
        Assert.Equal(LogLevel.Information, scriptEntry.Level);
        Assert.Contains("budgeting/check_budget_status", scriptEntry.Message);
        Assert.Contains("Food", scriptEntry.Message);
        Assert.Contains("status: ok", scriptEntry.Message);
        Assert.Contains(UserId.ToString(), scriptEntry.Message);
    }

    [Fact]
    public async Task LongResult_IsTruncated_AndPayloadsCanBeOmitted()
    {
        var (agent, chatClient, logger) = Build(new SkillCallLoggingOptions { MaxPayloadChars = 50 });
        var session = await agent.CreateSessionAsync();
        chatClient.Enqueue(RunScript("budgeting", "long_report"), FinalText("done"));
        await DrainAsync(agent.RunStreamingAsync("report", session));

        var truncated = Assert.Single(logger.Entries, e => e.Message.Contains("long_report"));
        Assert.Contains("…(+", truncated.Message);
        Assert.DoesNotContain(new string('x', 51), truncated.Message);

        var (quietAgent, quietClient, quietLogger) = Build(new SkillCallLoggingOptions { IncludePayloads = false });
        var quietSession = await quietAgent.CreateSessionAsync();
        quietClient.Enqueue(RunScript("budgeting", "check_budget_status"), FinalText("done"));
        await DrainAsync(quietAgent.RunStreamingAsync("check", quietSession));

        var omitted = Assert.Single(quietLogger.Entries, e => e.Message.Contains("check_budget_status"));
        Assert.Contains("(omitted)", omitted.Message);
        Assert.DoesNotContain("status: ok", omitted.Message);
    }

    [Fact]
    public async Task RejectedScript_IsNotLogged_ApprovedScript_IsLogged()
    {
        var (agent, chatClient, logger) = Build(new SkillCallLoggingOptions());
        var session = await agent.CreateSessionAsync();

        // add_transaction is classified Write and autoApproveWrites is false — stops on an approval request.
        chatClient.Enqueue(RunScript("budgeting", "add_transaction"), FinalText("not added"));
        var pending = await FindApprovalRequestAsync(agent.RunStreamingAsync("add $10", session));
        await DrainAsync(agent.RunStreamingAsync(new ChatMessage(ChatRole.User, [pending.CreateResponse(approved: false, reason: null)]), session));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("add_transaction"));

        chatClient.Enqueue(RunScript("budgeting", "add_transaction"), FinalText("added"));
        pending = await FindApprovalRequestAsync(agent.RunStreamingAsync("add $10 again", session));
        await DrainAsync(agent.RunStreamingAsync(new ChatMessage(ChatRole.User, [pending.CreateResponse(approved: true, reason: null)]), session));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("add_transaction"));
        Assert.Contains("added", entry.Message);
    }

    [Fact]
    public async Task ThrowingScript_LogsWarningWithException()
    {
        var (agent, chatClient, logger) = Build(new SkillCallLoggingOptions());
        var session = await agent.CreateSessionAsync();
        chatClient.Enqueue(RunScript("budgeting", "broken"), FinalText("sorry"));
        await DrainAsync(agent.RunStreamingAsync("break it", session));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("broken"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("failed", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Exception, exactMatch: false);
    }

    private static (AIAgent Agent, ScriptedChatClient ChatClient, CapturingLogger Logger) Build(SkillCallLoggingOptions options)
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
        var toolApprovalOptions = new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [SkillApprovalPolicy.BuildAutoApprovalRule(autoApproveWrites: false, autoApproveExecuteScript: true)],
        };

        var logger = new CapturingLogger();
        var agent = new AgentFactory(chatClient)
            .CreateAgent(skillsProvider, "test", toolApprovalOptions)
            .WithSkillCallLogging(logger, UserId, options);
        return (agent, chatClient, logger);
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        await foreach (var _ in updates)
        {
        }
    }

    private static async Task<ToolApprovalRequestContent> FindApprovalRequestAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        ToolApprovalRequestContent? pending = null;
        await foreach (var update in updates)
        {
            pending = update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault() ?? pending;
        }

        return pending ?? throw new InvalidOperationException("Expected an approval request.");
    }

    private static ChatResponseUpdate LoadSkill(string skillName) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString(), AgentSkillsProvider.LoadSkillToolName,
            new Dictionary<string, object?> { ["skillName"] = skillName })])
        { FinishReason = ChatFinishReason.ToolCalls };

    private static ChatResponseUpdate RunScript(string skillName, string scriptName, Dictionary<string, object?>? arguments = null) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString(), AgentSkillsProvider.RunSkillScriptToolName,
            new Dictionary<string, object?>
            {
                ["skillName"] = skillName,
                ["scriptName"] = scriptName,
                ["arguments"] = arguments ?? new Dictionary<string, object?>(),
            })])
        { FinishReason = ChatFinishReason.ToolCalls };

    private static ChatResponseUpdate FinalText(string text) =>
        new(ChatRole.Assistant, text) { FinishReason = ChatFinishReason.Stop };

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class FakeBudgetingSkill : AgentClassSkill<FakeBudgetingSkill>
    {
        public FakeBudgetingSkill() : base(argumentMarshaler: null) { }
        public override AgentSkillFrontmatter Frontmatter { get; } = new("budgeting", "Fake budgeting skill for logging tests.", null);
        protected override string Instructions => "Use the scripts.";

        [AgentSkillScript("add_transaction")]
        public Task<string> AddTransactionAsync() => Task.FromResult("added");

        [AgentSkillScript("check_budget_status")]
        public Task<string> CheckBudgetStatusAsync(string? category = null) => Task.FromResult($"status: ok ({category})");

        [AgentSkillScript("long_report")]
        public Task<string> LongReportAsync() => Task.FromResult(new string('x', 500));

        [AgentSkillScript("broken")]
        public Task<string> BrokenAsync() => throw new InvalidOperationException("boom");
    }

    /// <summary>Same queue-of-scripted-model-turns fake as <see cref="HarnessAgentApprovalIntegrationTests"/>'s.</summary>
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
