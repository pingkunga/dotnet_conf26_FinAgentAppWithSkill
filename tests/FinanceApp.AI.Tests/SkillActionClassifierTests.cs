using FinanceApp.AI;

namespace FinanceApp.AI.Tests;

/// <summary>
/// Pure logic for <see cref="SkillActionClassifier.Classify"/> — the (skillName, scriptName) -> kind
/// table <see cref="SkillApprovalPolicy"/> delegates to. Proves both the known-script classifications and,
/// most importantly, the deliberate "unregistered = None = always approved" default (docs/spec.md's
/// approval-toggle plan) that lets a newly added skill run without breaking until someone explicitly wires
/// it into this table.
/// </summary>
public class SkillActionClassifierTests
{
    [Theory]
    [InlineData("budgeting", "add_transaction")]
    [InlineData("budgeting", "set_budget")]
    [InlineData("budgeting", "transfer_budget")]
    public void Classify_BudgetingWriteScripts_AreWrite(string skillName, string scriptName)
    {
        Assert.Equal(SkillActionKind.Write, SkillActionClassifier.Classify(skillName, scriptName));
    }

    [Fact]
    public void Classify_SavingsCalculatorScript_IsExecuteScript()
    {
        Assert.Equal(SkillActionKind.ExecuteScript,
            SkillActionClassifier.Classify("savings-calculator", "scripts/project-savings.py"));
    }

    [Fact]
    public void Classify_ReceiptOcrExtract_IsWrite_MatchedByPrefixRegardlessOfUploadGuid()
    {
        Assert.Equal(SkillActionKind.Write,
            SkillActionClassifier.Classify("receipt-ocr-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "extract_receipt"));
        Assert.Equal(SkillActionKind.Write,
            SkillActionClassifier.Classify("receipt-ocr-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "extract_receipt"));
    }

    [Theory]
    [InlineData("budgeting", "check_budget_status")]
    [InlineData("budgeting", "list_transactions")]
    [InlineData("savings-calculator", "some_other_script")]
    [InlineData("receipt-ocr-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "some_other_script")]
    public void Classify_KnownSkillButUnregisteredScript_IsNone(string skillName, string scriptName)
    {
        Assert.Equal(SkillActionKind.None, SkillActionClassifier.Classify(skillName, scriptName));
    }

    [Fact]
    public void Classify_UnregisteredSkill_IsNone_NotAnError()
    {
        // The "forgot to add a new skill to the table" case — must default to None (always auto-approved),
        // never throw or silently deny.
        Assert.Equal(SkillActionKind.None, SkillActionClassifier.Classify("some-new-skill", "some_new_script"));
    }

    [Fact]
    public void Classify_NullSkillOrScriptName_IsNone()
    {
        Assert.Equal(SkillActionKind.None, SkillActionClassifier.Classify(null, "add_transaction"));
        Assert.Equal(SkillActionKind.None, SkillActionClassifier.Classify("budgeting", null));
    }
}
