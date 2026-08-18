using Microsoft.Agents.AI;

namespace FinanceApp.AI;

/// <summary>
/// The single seam allowed to construct an <see cref="AIAgent"/> from the app's singleton
/// <c>IChatClient</c> (see docs/spec.md §3.3). Every finance feature builds its agent through this,
/// never by calling <c>AsHarnessAgent</c>/<c>AsAIAgent</c> directly — keeps the construction path
/// isolated to one implementation. Built via <c>Microsoft.Agents.AI.Harness</c>'s <c>AsHarnessAgent</c>
/// (docs/spec.md's approval-toggle plan) with every optional Harness capability besides Tool Approval +
/// this app's own skills wiring explicitly disabled — see <c>AgentFactory</c>'s remarks.
/// </summary>
public interface IAgentFactory
{
    /// <param name="skillsProvider">
    /// The combined skills source for this agent (file/class/inline/MCP — see docs/spec.md §4.4's
    /// <c>AgentSkillsProviderBuilder</c> composition).
    /// </param>
    /// <param name="instructions">System instructions for the agent; null uses the chat client's default.</param>
    /// <param name="toolApprovalOptions">
    /// Options for the auto-approval middleware Harness wraps the agent with internally (docs/spec.md's
    /// approval-toggle plan) — typically <see cref="SkillApprovalPolicy.BuildAutoApprovalRule"/> wired into
    /// <see cref="ToolApprovalAgentOptions.AutoApprovalRules"/>. Null uses Harness's own default settings.
    /// </param>
    AIAgent CreateAgent(AgentSkillsProvider skillsProvider, string? instructions = null, ToolApprovalAgentOptions? toolApprovalOptions = null);
}
