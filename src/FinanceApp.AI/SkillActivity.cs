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

    /// <summary>
    /// Human-readable label for a pending <see cref="ToolApprovalRequestContent"/>, for the Approve/Reject
    /// card in <c>Chat.razor</c>. Returns <see langword="null"/> for a shape this app never produces (a
    /// non-<see cref="FunctionCallContent"/> tool call) rather than throwing — same defensive posture as
    /// <see cref="Extract"/> above.
    /// </summary>
    public static string? DescribeApprovalRequest(ToolApprovalRequestContent request)
    {
        if (request.ToolCall is not FunctionCallContent call)
        {
            return null;
        }

        if (call.Name == AgentSkillsProvider.RunSkillScriptToolName)
        {
            var skillName = GetArg(call, "skillName") ?? "?";
            var scriptName = GetArg(call, "scriptName") ?? "?";
            return $"{skillName}: run {scriptName}";
        }

        if (call.Name == AgentSkillsProvider.LoadSkillToolName)
        {
            return $"load skill {GetArg(call, "skillName") ?? "?"}";
        }

        if (call.Name == AgentSkillsProvider.ReadSkillResourceToolName)
        {
            return $"{GetArg(call, "skillName") ?? "?"}: read {GetArg(call, "resourceName") ?? "?"}";
        }

        return call.Name;
    }
}

/// <summary>
/// What kind of trust-sensitive action a <c>run_skill_script</c> call performs, if any. <see cref="None"/>
/// is the default for anything not explicitly classified below — and always auto-approves; a newly added
/// skill that forgets to register itself in <see cref="SkillActionClassifier"/> just isn't gated, not an
/// error (deliberate "opt-in gating" choice, not a default-deny safety net).
/// </summary>
public enum SkillActionKind
{
    None,
    Write,
    ExecuteScript,
}

/// <summary>
/// Maps a <c>run_skill_script</c> call's <c>(skillName, scriptName)</c> to the kind of trust-sensitive
/// action it performs. This is a fixed, developer-owned table — not user-configurable — mirroring this
/// app's existing "hardcode the small fixed skill set" style (same reasoning as CLAUDE.md's firm "4 skill
/// types" mapping). Adding a new gateable script is one line here; forgetting to add one means it stays
/// <see cref="SkillActionKind.None"/> (always auto-approved), by design.
/// </summary>
public static class SkillActionClassifier
{
    private const string ReceiptOcrSkillPrefix = "receipt-ocr-";

    private static readonly Dictionary<(string SkillName, string ScriptName), SkillActionKind> Table =
        new Dictionary<(string, string), SkillActionKind>
        {
            [("budgeting", "add_transaction")] = SkillActionKind.Write,
            [("budgeting", "set_budget")] = SkillActionKind.Write,
            [("budgeting", "transfer_budget")] = SkillActionKind.Write,
            [("savings-calculator", "scripts/project-savings.py")] = SkillActionKind.ExecuteScript,
        };

    public static SkillActionKind Classify(string? skillName, string? scriptName)
    {
        if (skillName is null || scriptName is null)
        {
            return SkillActionKind.None;
        }

        // Receipt-OCR registers a uniquely-named skill instance per upload (see ReceiptOcrSkillFactory),
        // so it can't be a literal table entry — matched by prefix instead.
        if (skillName.StartsWith(ReceiptOcrSkillPrefix, StringComparison.Ordinal) && scriptName == "extract_receipt")
        {
            return SkillActionKind.Write;
        }

        return Table.TryGetValue((skillName, scriptName), out var kind) ? kind : SkillActionKind.None;
    }
}

/// <summary>
/// Decides which <c>run_skill_script</c> calls actually need a human's Approve/Reject click, given a user's
/// two per-action-kind preferences. Every <c>run_skill_script</c> call passes through the approval pipeline
/// unconditionally (<c>ChatSessionService</c> sets <c>DisableRunSkillScriptApproval = false</c> always) —
/// this policy is what makes that mechanism selectively silent: it auto-approves everything except scripts
/// that <see cref="SkillActionClassifier"/> classifies as <see cref="SkillActionKind.Write"/> or
/// <see cref="SkillActionKind.ExecuteScript"/> whose matching toggle is currently off. <c>load_skill</c>/
/// <c>read_skill_resource</c> are never gated at all, regardless of any toggle.
/// </summary>
public static class SkillApprovalPolicy
{
    public static Func<ToolAutoApprovalRuleContext, ValueTask<bool>> BuildAutoApprovalRule(
        bool autoApproveWrites, bool autoApproveExecuteScript) =>
        ctx => ValueTask.FromResult(IsAutoApproved(ctx.FunctionCallContent, autoApproveWrites, autoApproveExecuteScript));

    /// <summary>Exposed directly (not just via the delegate above) so it's unit-testable without standing up a <see cref="ToolAutoApprovalRuleContext"/>.</summary>
    public static bool IsAutoApproved(FunctionCallContent call, bool autoApproveWrites, bool autoApproveExecuteScript)
    {
        if (call.Name == AgentSkillsProvider.LoadSkillToolName || call.Name == AgentSkillsProvider.ReadSkillResourceToolName)
        {
            return true;
        }

        if (call.Name != AgentSkillsProvider.RunSkillScriptToolName)
        {
            return true;
        }

        var skillName = GetArgValue(call, "skillName");
        var scriptName = GetArgValue(call, "scriptName");

        return SkillActionClassifier.Classify(skillName, scriptName) switch
        {
            SkillActionKind.Write => autoApproveWrites,
            SkillActionKind.ExecuteScript => autoApproveExecuteScript,
            _ => true,
        };
    }

    private static string? GetArgValue(FunctionCallContent call, string key) =>
        call.Arguments is not null && call.Arguments.TryGetValue(key, out var value) ? value?.ToString() : null;
}
