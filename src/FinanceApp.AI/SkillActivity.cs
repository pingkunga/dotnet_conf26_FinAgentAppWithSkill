using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FinanceApp.AI;

/// <summary>
/// One observed skill-tool call, for <c>Chat.razor</c>'s <c>AgentActivityLog</c> panel (docs/spec.md §7).
/// </summary>
public sealed record SkillActivityEntry(string ToolName, string SkillName, string? Detail);

/// <summary>
/// Pulls skill-tool <see cref="FunctionCallContent"/> out of a streamed <see cref="AgentResponseUpdate"/> —
/// the mechanism required by docs/spec.md §7/§8 for proving a skill actually fired (inspect the tool-call
/// trace, not the agent's prose). Verified against the real package via a hand-rolled <c>IChatClient</c>
/// spike (2026-08-11, not just docs): <c>AsAIAgent</c>'s function-invoking middleware surfaces both
/// <see cref="FunctionCallContent"/> and its matching <see cref="FunctionResultContent"/> in the stream
/// (neither is <c>InformationalOnly</c> in this package version) — confirming the streaming design here is
/// sound. That same spike found tool-call approval is **on by default** for
/// <c>load_skill</c>/<c>read_skill_resource</c>/<c>run_skill_script</c> (surfaces
/// <c>ToolApprovalRequestContent</c> and the stream just stops, no exception) — this app has no approval UI,
/// so <c>ChatSessionService</c> disables it via <c>AgentSkillsProviderOptions</c>.
/// </summary>
public static class SkillActivityExtractor
{
    public static IEnumerable<SkillActivityEntry> Extract(AgentResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is not FunctionCallContent call)
            {
                continue;
            }

            if (call.Name == AgentSkillsProvider.LoadSkillToolName)
            {
                yield return new SkillActivityEntry("load_skill", GetArg(call, "skillName") ?? "?", null);
            }
            else if (call.Name == AgentSkillsProvider.ReadSkillResourceToolName)
            {
                yield return new SkillActivityEntry(
                    "read_skill_resource", GetArg(call, "skillName") ?? "?", GetArg(call, "resourceName"));
            }
            else if (call.Name == AgentSkillsProvider.RunSkillScriptToolName)
            {
                yield return new SkillActivityEntry(
                    "run_skill_script", GetArg(call, "skillName") ?? "?", GetArg(call, "scriptName"));
            }
        }
    }

    private static string? GetArg(FunctionCallContent call, string key) =>
        call.Arguments is not null && call.Arguments.TryGetValue(key, out var value) ? value?.ToString() : null;
}
