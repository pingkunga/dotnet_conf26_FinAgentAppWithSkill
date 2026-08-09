using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FinanceApp.AI;

/// <summary>
/// Constructs <see cref="AIAgent"/>s from the app's singleton <see cref="IChatClient"/> (docs/spec.md
/// §3.3). Verified against the real <c>Microsoft.Agents.AI</c> 1.17.0 / <c>Microsoft.Extensions.AI</c>
/// 10.8.3 packages (spike in scratchpad, 2026-08-10): <c>IChatClient.AsAIAgent(...)</c>
/// (<c>Microsoft.Extensions.AI.ChatClientExtensions</c>) is a **universal** extension method — it works
/// for any <see cref="IChatClient"/>, not just Responses-API-capable ones. This resolves the spec's
/// "one genuinely open question" favorably: every provider (Azure/OpenAI/Ollama/Gemini/Anthropic)
/// reaches <see cref="AIAgent"/> through this exact same call, so the originally-planned per-provider
/// graceful-degradation fallback ("mark that provider's chat as unavailable") is not needed at this
/// layer — every <see cref="IChatClient"/> that <c>ChatClientFactory</c> can produce works here.
/// </summary>
/// <remarks>
/// One API detail that didn't match the spec's guess: <see cref="ChatClientAgentOptions"/> has no
/// <c>Instructions</c> property of its own — instructions are set via
/// <see cref="ChatClientAgentOptions.ChatOptions"/>.<see cref="ChatOptions.Instructions"/> instead
/// (confirmed by inspecting it round-trips to <c>AIAgent.Instructions</c> after construction).
/// </remarks>
public sealed class AgentFactory(IChatClient chatClient, ILoggerFactory? loggerFactory = null) : IAgentFactory
{
    public AIAgent CreateAgent(AgentSkillsProvider skillsProvider, string? instructions = null)
    {
        var options = new ChatClientAgentOptions
        {
            AIContextProviders = [skillsProvider],
            ChatOptions = instructions is null ? null : new ChatOptions { Instructions = instructions },
        };

        return chatClient.AsAIAgent(options, loggerFactory);
    }
}
