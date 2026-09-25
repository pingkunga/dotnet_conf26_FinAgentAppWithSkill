using System.Runtime.CompilerServices;
using System.Text.Json;
using FinanceApp.AI;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Skills;
using FinanceApp.Skills.Budgeting;
using FinanceApp.Skills.ExchangeRates;
using FinanceApp.Skills.ReceiptOcr;
using Microsoft.AspNetCore.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace FinanceApp.Web.Services;

/// <summary>
/// Builds one <see cref="AIAgent"/> + one <see cref="AgentSession"/> per Blazor circuit (docs/spec.md §3.4)
/// — registered scoped, not singleton, because the skills it wires close over DB-backed services via
/// <see cref="IServiceScopeFactory"/>. <c>Chat.razor</c> is the only consumer.
/// </summary>
/// <remarks>
/// <see cref="FinanceApp.Skills.Budgeting.BudgetSkill"/> (class-based), two file-based skills
/// (<c>skills/savings-goals</c>, no scripts; <c>skills/savings-calculator</c>, backed by
/// <see cref="SubprocessScriptRunner"/>), receipt-OCR (inline, docs/spec.md §4.2), and the MCP-based
/// monthly-summary skill (docs/spec.md §4.4, via <see cref="McpServerLauncher"/>) are all wired — all 4
/// skill source types the app demonstrates.
/// </remarks>
public sealed class ChatSessionService(
    IAgentFactory agentFactory,
    ICurrentUserAccessor currentUserAccessor,
    IServiceScopeFactory scopeFactory,
    IChatClient chatClient,
    AiOptions aiOptions,
    McpServerLauncher mcpServerLauncher,
    UserManager<ApplicationUser> userManager,
    SkillCallLoggingOptions skillCallLoggingOptions,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private const string SystemInstructions =
        "You are a helpful personal finance assistant for this app. Use the available skills to look up " +
        "or record the user's own data — never claim to have information you didn't actually retrieve via " +
        "a skill, and never reference another user's data under any circumstance.";

    private AIAgent? _agent;
    private AgentSession? _session;

    // per session, not from which process this is.
    private McpClient? _mcpClient;

    // This session's own archive-skill extraction directory (McpSkillsExtractionDirectory) — deleted in
    // DisposeAsync, so extracted MCP archive skills don't pile up as GUID folders in the working directory.
    private string? _mcpSkillsDirectory;

    // Backing list for receipt-OCR inline skills, registered dynamically as the user uploads receipts
    // (docs/spec.md §4.2) — read by DynamicInlineSkillsSource below on every turn (paired with
    // .DisableCaching() on the builder), not snapshotted once at agent-build time. Confirmed via a spike:
    // a skill added here mid-conversation is visible to load_skill on the very next turn, with no agent/
    // session rebuild and therefore no lost chat history — the alternative (rebuild per upload) would have
    // discarded the running AgentSession.
    private readonly List<AgentSkill> _dynamicSkills = [];

    public IAsyncEnumerable<AgentResponseUpdate> SendAsync(string message, CancellationToken cancellationToken) =>
        SendAsyncCore(message, cancellationToken);

    /// <summary>
    /// Resumes a run that stopped on a pending <see cref="ToolApprovalRequestContent"/> — the approval
    /// pipeline surfaces those instead of executing the gated <c>run_skill_script</c> call
    /// (<see cref="SkillApprovalPolicy"/>), and the stream simply ends with no exception (confirmed via the
    /// same 2026-08-11 spike <see cref="SkillActivityExtractor"/>'s doc comment references). Feeding the
    /// matching <see cref="ToolApprovalResponseContent"/> back in as a single <see cref="ChatMessage"/>
    /// continues the same <see cref="AgentSession"/> rather than starting a new turn.
    /// </summary>
    public IAsyncEnumerable<AgentResponseUpdate> ResumeWithApprovalAsync(
        ToolApprovalResponseContent response, CancellationToken cancellationToken) =>
        ResumeWithApprovalAsyncCore(response, cancellationToken);

    /// <summary>
    /// Builds a fresh receipt-OCR inline skill for <paramref name="receiptId"/> (closing over the app's
    /// <see cref="IChatClient"/> and the current user, per <see cref="ReceiptOcrSkillFactory"/>'s contract)
    /// and adds it to the running session — callable any time after the circuit exists, including before
    /// <c>Chat.razor</c> has ever built an agent (the list is a field on this scoped service, not something
    /// <see cref="GetOrCreateAgent"/> creates fresh each time). Not "Async" in name — nothing here actually
    /// awaits; the OCR work itself happens later, inside the skill's script, when the agent calls it.
    /// </summary>
    public void RegisterReceiptSkill(Guid receiptId)
    {
        var userId = currentUserAccessor.UserId
            ?? throw new InvalidOperationException("ChatSessionService requires an authenticated user.");

        _dynamicSkills.Add(ReceiptOcrSkillFactory.Create(chatClient, scopeFactory, userId, receiptId, aiOptions.SupportsVision));
    }

    private async IAsyncEnumerable<AgentResponseUpdate> SendAsyncCore(
        string message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agent = await GetOrCreateAgentAsync(cancellationToken);
        _session ??= await agent.CreateSessionAsync(cancellationToken);

        await foreach (var update in agent.RunStreamingAsync(message, _session, cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> ResumeWithApprovalAsyncCore(
        ToolApprovalResponseContent response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_agent is null || _session is null)
        {
            throw new InvalidOperationException("Cannot resume an approval before a session has been started.");
        }

        var message = new ChatMessage(ChatRole.User, [response]);
        await foreach (var update in _agent.RunStreamingAsync(message, _session, cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    private async Task<AIAgent> GetOrCreateAgentAsync(CancellationToken cancellationToken)
    {
        if (_agent is not null)
        {
            return _agent;
        }

        // Resolved lazily here, not captured in a field/constructor — AuthStateCurrentUserAccessor can
        // legitimately return null before the circuit's auth state is set (docs/spec.md §2a point 4's
        // AuthStateCurrentUserAccessor comment); failing fast here is clearer than silently building an
        // agent with no user scope.
        var userId = currentUserAccessor.UserId
            ?? throw new InvalidOperationException("ChatSessionService requires an authenticated user.");

        // The per-skill approval preferences (docs/spec.md's approval-toggle plan) live on ApplicationUser
        // itself, not on ICurrentUserAccessor — resolved once here, same "cached on _agent for the rest of
        // this circuit" lifetime as everything else built in this method. A mid-session Profile change
        // takes effect next circuit, not retroactively — an accepted limitation, not a bug.
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("ChatSessionService requires a resolvable ApplicationUser.");

        var budgetSkill = new BudgetSkill(scopeFactory, userId);
        var exchangeRateSkill = new ExchangeRateSkill(scopeFactory);
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");

        // Attempted once per session, guarded by the _agent is not null check above. Graceful degradation
        // on any failure — _mcpClient stays null, .UseMcpSkills(...) below is skipped entirely, chat still
        // works with the other 3 skill sources (docs/spec.md §4.4).
        _mcpClient = await mcpServerLauncher.TryStartAsync(userId, cancellationToken);

        var skillsBuilder = new AgentSkillsProviderBuilder()
            .UseSkill(budgetSkill)
            .UseSkill(exchangeRateSkill)
            // savings-goals (docs/spec.md §4.3): guidance-only, no scripts/ folder — the runner exists only
            .UseFileSkill(Path.Combine(skillsRoot, "savings-goals"), options: null, scriptRunner: SubprocessScriptRunner.RunAsync)
            // savings-calculator (docs/spec.md §4.3): the deliberately script-capable counterpart —
            .UseFileSkill(Path.Combine(skillsRoot, "savings-calculator"), options: null, scriptRunner: SubprocessScriptRunner.RunAsync)
            // .UseFileScriptRunner(SubprocessScriptRunner.RunAsync)
            // receipt-OCR (docs/spec.md §4.2): dynamic source over _dynamicSkills, re-read every turn
            .UseSource(_ => new DynamicInlineSkillsSource(_dynamicSkills))
            //DisableCaching rather than snapshotted once — the whole point is that RegisterReceiptSkill
            .DisableCaching();

        if (_mcpClient is not null)
        {
            // Get Skill from MCP server such as monthly-summary 
            _mcpSkillsDirectory = McpSkillsExtractionDirectory.CreateForSession();
            skillsBuilder = skillsBuilder.UseMcpSkills(_mcpClient, new AgentMcpSkillsSourceOptions
            {
                ArchiveSkillsDirectory = _mcpSkillsDirectory,
            });
        }

        var skillsProvider = skillsBuilder
            .UseOptions(o =>
            {
                o.DisableLoadSkillApproval = true;
                o.DisableReadSkillResourceApproval = true;
                o.DisableRunSkillScriptApproval = false;   //Required Approval Rule for running skill scripts ToolApprovalAgentOptions
            })
            .Build();

        // Wired into AgentFactory's HarnessAgentOptions.ToolApprovalAgentOptions
        // an approval prompt now that DisableRunSkillScriptApproval is false above.
        var toolApprovalOptions = new ToolApprovalAgentOptions
        {
            // By pass
            // AutoApprovalRules = [AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule],
            AutoApprovalRules =
            [
                SkillApprovalPolicy.BuildAutoApprovalRule(user.AutoApproveWrites, user.AutoApproveExecuteScript),
            ],
        };

        // Logs every executed skill call's arguments + result (all 4 skill source types, MCP included) —
        // wrapped here rather than in AgentFactory because this is where the authenticated userId lives.
        _agent = agentFactory.CreateAgent(skillsProvider, SystemInstructions, toolApprovalOptions)
            .WithSkillCallLogging(loggerFactory.CreateLogger(typeof(SkillCallLogging)), userId, skillCallLoggingOptions);

        return _agent;
    }

    /// <summary>
    /// Closes this session's HTTP connection to <c>FinanceApp.McpServer</c> (if one was opened) when the
    /// owning Blazor circuit's DI scope is torn down — <see cref="ChatSessionService"/> is registered
    /// scoped, so the container calls this automatically; no explicit hook-up needed elsewhere. No process
    /// to reap anymore (docs/spec.md §5, Step 1) — the server is a standing service, unaffected by any one
    /// session ending. Also deletes this session's extracted archive-skill directory.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        McpSkillsExtractionDirectory.TryDelete(_mcpSkillsDirectory, loggerFactory.CreateLogger<ChatSessionService>());
    }

    /// <summary>
    /// Script runner for <c>skills/savings-goals</c>, which deliberately has no <c>scripts/</c> folder.
    /// <c>AgentSkillsProviderBuilder.UseFileSkill(...)</c> requires a non-null runner regardless of whether
    /// the skill has any scripts to run — confirmed via a spike, contradicting an official devblog that
    /// claimed omitting it is fine in that case — so this exists purely to satisfy that requirement and
    /// should never actually be invoked.
    /// </summary>
    private static Task<object?> NoScriptsRunner(
        AgentFileSkill skill, AgentFileSkillScript script, JsonElement? arguments,
        IServiceProvider? serviceProvider, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"'{skill.Frontmatter.Name}' has no scripts/ folder; '{script.Name}' should never be invoked.");

    /// <summary>
    /// Reads a live <see cref="List{AgentSkill}"/> reference on every call — paired with the builder's
    /// <c>.DisableCaching()</c> (docs/spec.md §4.2), this is what lets <see cref="RegisterReceiptSkill"/>
    /// join a conversation already in progress. Confirmed via a spike, not assumed: without
    /// DisableCaching, a source's <see cref="GetSkillsAsync"/> result is snapshotted once.
    /// </summary>
    private sealed class DynamicInlineSkillsSource(List<AgentSkill> skills) : AgentSkillsSource
    {
        public override Task<IList<AgentSkill>> GetSkillsAsync(AgentSkillsSourceContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IList<AgentSkill>>(skills.ToList());
    }
}
