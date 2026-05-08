using OpenWorkmate.Server;
using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ToolPermissionRuleEvaluatorTests
{
    [Fact]
    public void Evaluate_DenyWinsOverAllow()
    {
        var rules = new List<ToolPermissionRule>
        {
            new() { Pattern = "CLI:*", Effect = "allowAlways" },
            new() { Pattern = "CLI:run_command", Effect = "deny" }
        };
        var e = ToolPermissionRuleEvaluator.Evaluate(rules, "CLI", "run_command");
        Assert.Equal(ToolPermissionRuleEffect.Deny, e);
    }

    [Fact]
    public void Evaluate_AskWinsOverAllow()
    {
        var rules = new List<ToolPermissionRule>
        {
            new() { Pattern = "CLI:*", Effect = "allowAlways" },
            new() { Pattern = "*:run_command", Effect = "ask" }
        };
        var e = ToolPermissionRuleEvaluator.Evaluate(rules, "CLI", "run_command");
        Assert.Equal(ToolPermissionRuleEffect.Ask, e);
    }

    [Fact]
    public void Evaluate_GlobStar()
    {
        var rules = new List<ToolPermissionRule> { new() { Pattern = "Excel:excel_*", Effect = "deny" } };
        Assert.Equal(ToolPermissionRuleEffect.Deny, ToolPermissionRuleEvaluator.Evaluate(rules, "Excel", "excel_range_read"));
        Assert.Equal(ToolPermissionRuleEffect.None, ToolPermissionRuleEvaluator.Evaluate(rules, "Word", "word_body_read"));
    }

    [Fact]
    public void ApplyToNeedHitl_AskForcesTrue_AllowClears()
    {
        var need = false;
        ToolPermissionRuleEvaluator.ApplyToNeedHitl(ref need, ToolPermissionRuleEffect.Ask);
        Assert.True(need);
        need = true;
        ToolPermissionRuleEvaluator.ApplyToNeedHitl(ref need, ToolPermissionRuleEffect.AllowAlways);
        Assert.False(need);
    }
}
