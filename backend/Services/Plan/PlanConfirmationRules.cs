namespace OfficeCopilot.Server.Services.Plan;

/// <summary>计划生成后是否在 UI 中要求用户确认（与执行期 SecurityFilter/cliRunMode 无关）。</summary>
public static class PlanConfirmationRules
{
    /// <summary>步数大于 <paramref name="autoExecuteMaxSteps"/> 时需确认；步数 ≤ 阈值则可直接继续。</summary>
    public static bool RequiresUserConfirmation(int stepCount, int autoExecuteMaxSteps)
    {
        if (autoExecuteMaxSteps < 1) autoExecuteMaxSteps = 3;
        return stepCount > autoExecuteMaxSteps;
    }
}
