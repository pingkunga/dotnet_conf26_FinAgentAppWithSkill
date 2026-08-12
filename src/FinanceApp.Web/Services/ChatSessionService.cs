using System.Runtime.CompilerServices;
using FinanceApp.AI;
using FinanceApp.Core.Abstractions;
using FinanceApp.Skills.Budgeting;
using Microsoft.Agents.AI;

namespace FinanceApp.Web.Services;

/// <summary>
/// Builds one <see cref="AIAgent"/> + one <see cref="AgentSession"/> per Blazor circuit (docs/spec.md §3.4)
/// — registered scoped, not singleton, because the skills it wires close over DB-backed services via
/// <see cref="IServiceScopeFactory"/>. <c>Chat.razor</c> is the only consumer.
/// </summary>
/// <remarks>
/// Only <see cref="FinanceApp.Skills.Budgeting.BudgetSkill"/> is wired so far — the other three skill
/// sources (inline receipt-OCR, file-based savings-goals, MCP monthly-summary) don't exist yet (docs/spec.md
/// §4.2/§4.3/§4.4); adding a <c>.UseFileSkill(...)</c> call against a `skills/` directory that doesn't exist
/// would throw or silently produce nothing, so those are left as comments, not speculative calls.
/// </remarks>
public sealed class ChatSessionService(
    IAgentFactory agentFactory,
    ICurrentUserAccessor currentUserAccessor,
    IServiceScopeFactory scopeFactory)
{
    private const string SystemInstructions =
        "You are a helpful personal finance assistant for this app. Use the available skills to look up " +
        "or record the user's own data — never claim to have information you didn't actually retrieve via " +
        "a skill, and never reference another user's data under any circumstance.";

    private AIAgent? _agent;
    private AgentSession? _session;

    public IAsyncEnumerable<AgentResponseUpdate> SendAsync(string message, CancellationToken cancellationToken) =>
        SendAsyncCore(message, cancellationToken);

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

        var skillsProvider = new AgentSkillsProviderBuilder()
            .UseSkill(budgetSkill)
            // TODO(spec §4.2): .UseSkill(receiptOcrInlineSkill) — built per-upload from ReceiptUpload.razor,
            // not appropriate to wire into a general-purpose session agent built here.
            // TODO(spec §4.3): .UseFileSkill("skills/savings-goals") once that SKILL.md exists.
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
}
