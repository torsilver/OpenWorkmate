namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>
/// 动态工具「未激活则全量兜底」判定：供主会话 Runner 与单元测试共用。
/// </summary>
public static class DynamicToolingFallbackEvaluator
{
    /// <summary>
    /// 是否应在动态工具外层循环结束后追加一轮全量允许工具 pass。
    /// </summary>
    public static bool ShouldFallbackToFullAllowlist(DynamicToolingTurnState dts)
    {
        if (!dts.Config.FallbackToFullAllowlistWhenNoActivation)
            return false;
        if (dts.HasActivatedAnyBusinessTool())
            return false;
        if (dts.EffectfulNonMetaToolInvokedThisTurn)
            return false;
        return true;
    }
}
