using FinanceApp.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FinanceApp.AI.Tests;

/// <summary>
/// Pure logic — no <see cref="IChatClient"/>, no DB, no <see cref="ToolAutoApprovalRuleContext"/> needed,
/// since <see cref="SkillApprovalPolicy.IsAutoApproved"/> only inspects a <see cref="FunctionCallContent"/>
/// plus the two per-action-kind preferences. Proves the two toggles are independent (docs/spec.md's
/// approval-toggle plan), not one aggregate switch — see <see cref="SkillActionClassifierTests"/> for the
/// (skillName, scriptName) -> kind mapping this policy delegates to.
/// </summary>
public class SkillApprovalPolicyTests
{
    private static FunctionCallContent RunSkillScript(string skillName, string scriptName) =>
        new("call-1", AgentSkillsProvider.RunSkillScriptToolName,
            new Dictionary<string, object?> { ["skillName"] = skillName, ["scriptName"] = scriptName });

    [Theory]
    [InlineData("add_transaction")]
    [InlineData("set_budget")]
    [InlineData("transfer_budget")]
    public void BudgetingWriteScripts_GatedOnlyByTheWritesToggle(string scriptName)
    {
        var call = RunSkillScript("budgeting", scriptName);

        Assert.False(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: false, autoApproveExecuteScript: true));
        Assert.True(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: true, autoApproveExecuteScript: false));
    }

    [Theory]
    [InlineData("check_budget_status")]
    [InlineData("list_transactions")]
    public void BudgetingReadScripts_NeverGated_RegardlessOfEitherToggle(string scriptName)
    {
        var call = RunSkillScript("budgeting", scriptName);

        Assert.True(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: false, autoApproveExecuteScript: false));
    }

    [Fact]
    public void ReceiptOcrExtract_GatedOnlyByTheWritesToggle_MatchedByPrefixAcrossUploads()
    {
        // Each upload registers a uniquely-named instance (receipt-ocr-<guid>) — the policy must match by
        // prefix, not an exact skill name, or every upload after the first would silently stop being gated.
        var call = RunSkillScript("receipt-ocr-0123456789abcdef0123456789abcdef", "extract_receipt");

        Assert.False(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: false, autoApproveExecuteScript: true));
        Assert.True(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: true, autoApproveExecuteScript: false));
    }

    [Fact]
    public void SavingsCalculatorScript_GatedOnlyByTheExecuteScriptToggle()
    {
        var call = RunSkillScript("savings-calculator", "scripts/project-savings.py");

        Assert.False(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: true, autoApproveExecuteScript: false));
        Assert.True(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: false, autoApproveExecuteScript: true));
    }

    [Fact]
    public void UnclassifiedSkillScript_AlwaysAutoApproved_RegardlessOfEitherToggle()
    {
        // A skill that was never added to SkillActionClassifier's table (e.g. a newly added skill nobody
        // wired approval for yet) must default to auto-approved, not silently blocked — the "opt-in gating"
        // philosophy the whole toggle system is built on.
        var call = RunSkillScript("some-new-skill", "some_new_script");

        Assert.True(SkillApprovalPolicy.IsAutoApproved(call, autoApproveWrites: false, autoApproveExecuteScript: false));
    }

    [Theory]
    [InlineData("skill-index")]
    [InlineData("monthly-summary")]
    public void LoadSkillAndReadSkillResource_NeverGated_EvenForMcp(string skillName)
    {
        var load = new FunctionCallContent("call-1", AgentSkillsProvider.LoadSkillToolName,
            new Dictionary<string, object?> { ["skillName"] = skillName });
        var read = new FunctionCallContent("call-2", AgentSkillsProvider.ReadSkillResourceToolName,
            new Dictionary<string, object?> { ["skillName"] = skillName, ["resourceName"] = "whatever" });

        Assert.True(SkillApprovalPolicy.IsAutoApproved(load, autoApproveWrites: false, autoApproveExecuteScript: false));
        Assert.True(SkillApprovalPolicy.IsAutoApproved(read, autoApproveWrites: false, autoApproveExecuteScript: false));
    }
}
