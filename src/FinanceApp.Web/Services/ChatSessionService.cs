using System.Runtime.CompilerServices;
using System.Text.Json;
using FinanceApp.AI;
using FinanceApp.Core.Abstractions;
using FinanceApp.Skills;
using FinanceApp.Skills.Budgeting;
using FinanceApp.Skills.ReceiptOcr;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FinanceApp.Web.Services;

/// <summary>
/// Builds one <see cref="AIAgent"/> + one <see cref="AgentSession"/> per Blazor circuit (docs/spec.md §3.4)
/// — registered scoped, not singleton, because the skills it wires close over DB-backed services via
/// <see cref="IServiceScopeFactory"/>. <c>Chat.razor</c> is the only consumer.
/// </summary>
/// <remarks>
/// <see cref="FinanceApp.Skills.Budgeting.BudgetSkill"/> (class-based), two file-based skills
/// (<c>skills/savings-goals</c>, no scripts; <c>skills/savings-calculator</c>, backed by
/// <see cref="SubprocessScriptRunner"/>), and receipt-OCR (inline, docs/spec.md §4.2) are wired. The MCP
/// monthly-summary skill doesn't exist yet (docs/spec.md §4.4) — left as a comment, not a speculative call.
/// </remarks>
public sealed class ChatSessionService(
    IAgentFactory agentFactory,
    ICurrentUserAccessor currentUserAccessor,
    IServiceScopeFactory scopeFactory,
    IChatClient chatClient,
    AiOptions aiOptions)
{
    private const string SystemInstructions =
        "You are a helpful personal finance assistant for this app. Use the available skills to look up " +
        "or record the user's own data — never claim to have information you didn't actually retrieve via " +
        "a skill, and never reference another user's data under any circumstance.";

    private AIAgent? _agent;
    private AgentSession? _session;

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
        var agent = GetOrCreateAgent();
        _session ??= await agent.CreateSessionAsync(cancellationToken);

        await foreach (var update in agent.RunStreamingAsync(message, _session, cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    private AIAgent GetOrCreateAgent()
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

        var budgetSkill = new BudgetSkill(scopeFactory, userId);
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");

        var skillsProvider = new AgentSkillsProviderBuilder()
            .UseSkill(budgetSkill)
            // TODO(spec §4.2): .UseSkill(receiptOcrInlineSkill) — built per-upload from ReceiptUpload.razor,
            // not appropriate to wire into a general-purpose session agent built here.
            // savings-goals (docs/spec.md §4.3): guidance-only, no scripts/ folder — the runner exists only
            // because .UseFileSkill(...) requires a non-null one even when there's nothing to ever invoke
            // (found via a spike: it throws InvalidOperationException at .Build() otherwise).
            .UseFileSkill(Path.Combine(skillsRoot, "savings-goals"), options: null, scriptRunner: NoScriptsRunner)
            // savings-calculator (docs/spec.md §4.3): the deliberately script-capable counterpart —
            // SubprocessScriptRunner is trusted here because this whole path is developer-authored content
            // shipped with the app (never a user upload; see SubprocessScriptRunner.cs's remarks).
            .UseFileSkill(Path.Combine(skillsRoot, "savings-calculator"), options: null, scriptRunner: SubprocessScriptRunner.RunAsync)
            // receipt-OCR (docs/spec.md §4.2): dynamic source over _dynamicSkills, re-read every turn
            // (DisableCaching) rather than snapshotted once — the whole point is that RegisterReceiptSkill
            // can add a skill after this provider is already built and in use. Cost accepted: the file
            // skills above lose their discovery cache too, since caching is a builder-wide setting — a
            // few extra small file reads per turn, trivial at this app's scale.
            .UseSource(_ => new DynamicInlineSkillsSource(_dynamicSkills))
            .DisableCaching()
            // TODO(spec §4.4): .UseMcpSkills(mcpClient) once FinanceApp.McpServer + McpServerLauncher exist,
            // omitted if the launcher's Client is null (graceful degradation).
            .UseOptions(o =>
            {
                // This app has no tool-approval UI (never designed one) — auto-approve. Every script is
                // already hard-scoped to `userId` captured above regardless of approval (docs/spec.md §2a
                // point 7), so the approval gate would only ever be a no-op confirmation, not a security
                // boundary. Found via a scratchpad spike (2026-08-11): without this, load_skill/
                // run_skill_script emit a ToolApprovalRequestContent and the stream just stops — no
                // exception, chat looks permanently "stuck".
                o.DisableLoadSkillApproval = true;
                o.DisableReadSkillResourceApproval = true;
                o.DisableRunSkillScriptApproval = true;
            })
            .Build();

        _agent = agentFactory.CreateAgent(skillsProvider, SystemInstructions);
        return _agent;
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
