using Microsoft.Agents.AI;

namespace FinanceApp.AI;

/// <summary>
/// The single seam allowed to construct an <see cref="AIAgent"/> from the app's singleton
/// <c>IChatClient</c> (see docs/spec.md §3.3). Every finance feature builds its agent through this,
/// never by calling <c>AsAIAgent</c> directly — keeps the (now resolved, previously uncertain)
/// construction path isolated to one implementation.
/// </summary>
public interface IAgentFactory
{
    /// <param name="skillsProvider">
    /// The combined skills source for this agent (file/class/inline/MCP — see docs/spec.md §4.4's
    /// <c>AgentSkillsProviderBuilder</c> composition).
    /// </param>
    /// <param name="instructions">System instructions for the agent; null uses the chat client's default.</param>
    AIAgent CreateAgent(AgentSkillsProvider skillsProvider, string? instructions = null);
}
