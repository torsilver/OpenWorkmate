using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>
/// 在 <c>activate_tools</c> 已更新 <see cref="DynamicToolingTurnState.ActivatedFunctionNames"/> 后，
/// 将 <see cref="DynamicToolingTurnState.ToolListMutationTarget"/> 与 <see cref="SessionToolResolver.BuildDynamicActiveToolList"/> 对齐。
/// 由中间件与插件两侧调用，避免仅依赖 MAF 中间件顺序导致刷新未执行。
/// </summary>
public static class DynamicToolingToolListRefresher
{
    /// <summary>原地替换当前 pass 绑定到 MEAI <c>ChatOptions.Tools</c> 的列表内容。</summary>
    public static void RefreshMutationTargetAfterActivate(ToolRegistry registry, ILogger log)
    {
        var dts = DynamicToolingTurnScope.Current;
        if (dts is null)
        {
            log.LogDebug("[DynamicTools] RefreshMutationTarget skipped: DynamicToolingTurnScope.Current is null");
            return;
        }

        var target = dts.ToolListMutationTarget;
        if (target is null)
        {
            log.LogWarning(
                "[DynamicTools] RefreshMutationTarget skipped: ToolListMutationTarget is null session={SessionId} clientType={ClientType}",
                dts.SessionIdForTools ?? SessionContext.GetSessionId() ?? "?",
                dts.ClientTypeForTools ?? "?");
            return;
        }

        var beforeCount = target.Count;
        var fresh = SessionToolResolver.BuildDynamicActiveToolList(
            registry,
            dts,
            dts.ClientTypeForTools,
            dts.SessionIdForTools,
            dts.MergePlanIntoDynamicBootstrap,
            diagnosticLogger: log);

        var activatedSorted = dts.ActivatedFunctionNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var activatedPreview = string.Join(',', activatedSorted);
        if (activatedPreview.Length > 480)
            activatedPreview = activatedPreview[..480] + "…";

        var nameList = fresh.Select(t => t.Name ?? "?").ToArray();
        var preview = string.Join(',', nameList.Take(28));
        if (nameList.Length > 28)
            preview += ",…";

        target.Clear();
        target.AddRange(fresh);

        dts.ClearExpansionFlag();

        log.LogInformation(
            "[DynamicTools] ChatOptions.Tools refreshed after activate_tools session={SessionId} clientType={ClientType} mergePlan={MergePlan} beforeCount={Before} afterCount={After} activatedCount={ActivatedCount} activated={ActivatedPreview} toolsPreview={ToolsPreview}",
            dts.SessionIdForTools ?? SessionContext.GetSessionId() ?? "?",
            dts.ClientTypeForTools ?? "?",
            dts.MergePlanIntoDynamicBootstrap,
            beforeCount,
            fresh.Count,
            activatedSorted.Length,
            activatedPreview,
            preview);
    }
}
