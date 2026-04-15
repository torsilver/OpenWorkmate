using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class DynamicToolingFallbackEvaluatorTests
{
    private static DynamicToolingTurnState CreateState(DynamicToolingConfig? cfg = null)
    {
        var reg = new ToolRegistry();
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        return new DynamicToolingTurnState(cfg ?? new DynamicToolingConfig(), catalog, SkillCatalogIndex.Empty);
    }

    [Fact]
    public void ShouldFallback_WhenOnlyMetaPath_DefaultConfig_True()
    {
        var dts = CreateState();
        dts.Config.FallbackToFullAllowlistWhenNoActivation = true;
        Assert.True(DynamicToolingFallbackEvaluator.ShouldFallbackToFullAllowlist(dts));
    }

    [Fact]
    public void ShouldFallback_WhenFallbackDisabled_False()
    {
        var dts = CreateState();
        dts.Config.FallbackToFullAllowlistWhenNoActivation = false;
        Assert.False(DynamicToolingFallbackEvaluator.ShouldFallbackToFullAllowlist(dts));
    }

    [Fact]
    public void ShouldFallback_WhenBusinessToolActivated_False()
    {
        var dts = CreateState();
        dts.Config.FallbackToFullAllowlistWhenNoActivation = true;
        dts.ActivatedFunctionNames.Add("excel_range_read");
        Assert.False(DynamicToolingFallbackEvaluator.ShouldFallbackToFullAllowlist(dts));
    }

    [Fact]
    public void ShouldFallback_WhenEffectfulNonMetaInvokedWithoutActivate_False()
    {
        var dts = CreateState();
        dts.Config.FallbackToFullAllowlistWhenNoActivation = true;
        dts.MarkEffectfulNonMetaInvocation("current_run_custom_document_script");
        Assert.False(DynamicToolingFallbackEvaluator.ShouldFallbackToFullAllowlist(dts));
    }

    [Fact]
    public void MarkEffectfulNonMetaInvocation_IgnoresMetaTools()
    {
        var dts = CreateState();
        dts.MarkEffectfulNonMetaInvocation(DynamicToolingConstants.SearchFunctionName);
        dts.MarkEffectfulNonMetaInvocation(DynamicToolingConstants.ActivateFunctionName);
        Assert.False(dts.EffectfulNonMetaToolInvokedThisTurn);
        Assert.True(DynamicToolingFallbackEvaluator.ShouldFallbackToFullAllowlist(dts));
    }
}
