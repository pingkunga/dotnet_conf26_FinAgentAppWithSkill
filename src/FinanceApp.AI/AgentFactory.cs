using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FinanceApp.AI;

/// <summary>
/// Constructs <see cref="AIAgent"/>s from the app's singleton <see cref="IChatClient"/> (docs/spec.md
/// §3.3), via <c>Microsoft.Agents.AI.Harness</c>'s <c>AsHarnessAgent</c> (docs/spec.md's approval-toggle
/// plan, superseding the earlier hand-assembled <c>AsAIAgent</c> + manual <c>ToolApprovalAgent</c> wrap).
/// <see cref="HarnessAgent"/> is a <see cref="DelegatingAIAgent"/> over <see cref="AIAgent"/>, so it's a
/// drop-in return type here — every provider (Azure/OpenAI/Ollama/Gemini/Anthropic) reaches it through
/// this exact same call, same as the prior <c>AsAIAgent</c> path (spike 2026-08-10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately "batteries mostly removed"</b>: <see cref="HarnessAgentOptions"/> defaults to a rich
/// set of capabilities (compaction, web search, todo provider, agent-mode provider, file memory,
/// OpenTelemetry, its own built-in file-based skill discovery) this app doesn't use — every one is
/// explicitly disabled below, keeping the running feature set identical to the pre-Harness design. Only
/// Tool Approval (<see cref="HarnessAgentOptions.ToolApprovalAgentOptions"/>) and this app's own
/// <see cref="AgentSkillsProvider"/> (passed via <see cref="HarnessAgentOptions.AIContextProviders"/>, not
/// <see cref="HarnessAgentOptions.AgentSkillsSource"/> — that property is a single raw
/// <see cref="AgentSkillsSource"/>, a different type from this app's already-composite
/// <see cref="AgentSkillsProvider"/>) are actually in play.
/// </para>
/// <para>
/// <see cref="HarnessAgentOptions.HarnessInstructions"/> is a different thing from this app's system
/// prompt — XML doc: "harness-level instructions that control general tool usage and behavior patterns".
/// The persona/system instructions still go via <see cref="ChatClientAgentOptions.ChatOptions"/>-style
/// <see cref="ChatOptions.Instructions"/> (same "no top-level <c>Instructions</c> property" surprise as
/// the prior <c>ChatClientAgentOptions</c>-based design).
/// </para>
/// <para>
/// Verified via a real streaming round-trip spike (2026-08-16, scratchpad, deleted after — not just XML
/// docs): a <see cref="HarnessAgent"/> built this way still surfaces <see cref="FunctionCallContent"/>
/// named exactly <see cref="AgentSkillsProvider.LoadSkillToolName"/>/<see cref="AgentSkillsProvider.
/// RunSkillScriptToolName"/> (what <c>SkillActivityExtractor</c>/<c>SkillApprovalPolicy</c> depend on),
/// that <see cref="ToolApprovalAgentOptions.AutoApprovalRules"/> is actually evaluated per call, that
/// resuming via <c>RunStreamingAsync(new ChatMessage(ChatRole.User, [response]), session, ...)</c> still
/// works, and — the highest-risk untested surface — that a skill registered into a mutable/dynamic
/// <see cref="AgentSkillsSource"/> mid-session (as <c>ChatSessionService.RegisterReceiptSkill</c> does for
/// receipt-OCR) is still discoverable on the very next turn.
/// </para>
/// <para>
/// One build-time gotcha the spike found: <see cref="HarnessAgentOptions"/> is currently marked
/// <c>[Experimental("MAAI001")]</c> in package version 1.17.0 (evaluation-purposes-only, subject to change)
/// despite Harness itself being announced GA — constructing it is a compile *error*, not just a warning,
/// without suppressing that diagnostic. Suppressed narrowly around the options construction below, not
/// project-wide.
/// </para>
/// </remarks>
public sealed class AgentFactory(IChatClient chatClient, ILoggerFactory? loggerFactory = null) : IAgentFactory
{
    public AIAgent CreateAgent(AgentSkillsProvider skillsProvider, string? instructions = null, ToolApprovalAgentOptions? toolApprovalOptions = null)
    {
        #pragma warning disable MAAI001 // HarnessAgentOptions is evaluation-purposes-only in this package version. DisableCompaction Error
        var options = new HarnessAgentOptions
        {
            ChatOptions = instructions is null ? null : new ChatOptions { Instructions = instructions },
            AIContextProviders = [skillsProvider],
            DisableAgentSkillsProvider = true,
            ToolApprovalAgentOptions = toolApprovalOptions,
            DisableCompaction = true,
            DisableFileMemory = true,
            DisableWebSearch = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableOpenTelemetry = true,
        };

        #pragma warning restore MAAI001
        return chatClient.AsHarnessAgent(options, loggerFactory);
    }
}
