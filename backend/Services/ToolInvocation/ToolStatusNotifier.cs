using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using OfficeCopilot.Server.Logging;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Chat;
using OfficeCopilot.Server.Services.Plan;

namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>
/// 工具调用前后向前端推送 tool_invocation_start / tool_invocation_end，
/// 并记录 audit log、debug stats、capability 日志。
/// 从已删除的 SK <c>ToolStatusFilter</c> 迁入，接口为 <see cref="IToolStatusNotifier"/>。
/// </summary>
public sealed class ToolStatusNotifier : IToolStatusNotifier
{
    private readonly SessionManager _sessionManager;
    private readonly ConfigService _configService;
    private readonly IPlanStore _planStore;
    private readonly ILogger<ToolStatusNotifier> _logger;
    private readonly AgentDebugStatsService _debugStats;
    private readonly TimelineBlockStreamCoordinator _timelineBlocks;

    public ToolStatusNotifier(
        SessionManager sessionManager,
        ConfigService configService,
        IPlanStore planStore,
        ILogger<ToolStatusNotifier> logger,
        AgentDebugStatsService debugStats,
        TimelineBlockStreamCoordinator timelineBlocks)
    {
        _sessionManager = sessionManager;
        _configService = configService;
        _planStore = planStore;
        _logger = logger;
        _debugStats = debugStats;
        _timelineBlocks = timelineBlocks;
    }

    public async Task<ToolStatusContext> BeforeInvocationAsync(
        string? sessionId, string pluginName, string functionName,
        IDictionary<string, object?> arguments, CancellationToken ct)
    {
        var cap = ToolCapabilityRegistry.Get(pluginName, functionName);
        _logger.LogDebug(
            "[ToolCapability] {Plugin}.{Function} readOnly={RO} destructive={D} suggestHitl={H} parallelOk={P}",
            pluginName, functionName, cap.ReadOnly, cap.Destructive, cap.SuggestHitl, cap.AllowParallelSameTurn);

        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning(
                "ToolStatusNotifier: 无 sessionId，无法推送 tool_invocation 状态。{Plugin}.{Function}",
                pluginName, functionName);
            return new ToolStatusContext { SessionId = null, PluginName = pluginName, FunctionName = functionName };
        }

        var startDetail = GetRunningDetail(functionName, arguments);
        var planStepIndex = GetPlanStepIndex(pluginName, functionName, arguments);

        await SendAgentPhaseAsync(sessionId, "intent", $"{pluginName}.{functionName}", ct).ConfigureAwait(false);

        var slowSummarySuffix = GetSlowIoSummarySuffix(pluginName, functionName);
        var slowAgentStatus = GetSlowIoAgentStatusLine(pluginName, functionName);
        var agentStatusJson = WsMessageJson.SerializeAgentStatus(slowAgentStatus);
        if (!string.IsNullOrEmpty(agentStatusJson))
            await _sessionManager.SendToAsync(sessionId, agentStatusJson, ct).ConfigureAwait(false);

        _timelineBlocks.OnToolInvocationStart(sessionId);
        await SendToolStatusAsync(sessionId, "tool_invocation_start", pluginName, functionName, null, null, startDetail, planStepIndex, slowSummarySuffix, ct).ConfigureAwait(false);
        _logger.LogDebug(
            "[ToolStatus] session={SessionId} pushed tool_invocation_start {Plugin}.{Function}",
            sessionId, pluginName, functionName);
        var ctxWin = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        SessionAuditLog.TryAppend(ctxWin, sessionId, "tool_invocation_start",
            new { plugin = pluginName, function = functionName, detail = SessionAuditLog.SanitizeForAudit(startDetail, 500) });

        IReadOnlyDictionary<string, object?>? argSnap = null;
        if (arguments is { Count: > 0 })
            argSnap = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);

        return new ToolStatusContext
        {
            SessionId = sessionId,
            PluginName = pluginName,
            FunctionName = functionName,
            Arguments = argSnap
        };
    }

    public async Task AfterInvocationAsync(ToolStatusContext ctx, object? result, bool success, CancellationToken ct)
    {
        var sessionId = ctx.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        var pluginName = ctx.PluginName;
        var functionName = ctx.FunctionName;

        var resultText = NormalizeToolResultToString(result);
        var content = "";
        if (!string.IsNullOrEmpty(resultText))
            content = resultText.Length <= 200 ? resultText : resultText[..200];

        var isSuccess = success && !IsToolResultFailure(content);
        _debugStats.RecordToolInvocation(pluginName, functionName, isSuccess);

        var planStepIndex = (string.Equals(pluginName, "Plan", StringComparison.OrdinalIgnoreCase)
            && string.Equals(functionName, "execute_plan_step", StringComparison.OrdinalIgnoreCase))
            ? null : (int?)null; // plan step index not available post-invocation without args; kept null

        await SendToolStatusAsync(sessionId, "tool_invocation_end", pluginName, functionName, isSuccess, content, null, null, null, ct).ConfigureAwait(false);
        var ctxWin = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        SessionAuditLog.TryAppend(ctxWin, sessionId, "tool_invocation_end",
            new { plugin = pluginName, function = functionName, success = isSuccess, resultPreview = SessionAuditLog.SanitizeForAudit(content, 500) });
        await SendAgentPhaseAsync(sessionId, "digest", isSuccess
            ? "已收到工具输出，继续处理…"
            : "工具返回异常，将据此调整后续步骤。", ct).ConfigureAwait(false);

        if (isSuccess
            && string.Equals(pluginName, "Plan", StringComparison.OrdinalIgnoreCase)
            && string.Equals(functionName, "create_plan", StringComparison.OrdinalIgnoreCase))
        {
            await TryEmitPlanCreatedAsync(sessionId, resultText);
        }

        if (isSuccess
            && string.Equals(pluginName, "Plan", StringComparison.OrdinalIgnoreCase)
            && string.Equals(functionName, "update_plan", StringComparison.OrdinalIgnoreCase))
        {
            await TryEmitPlanUpdatedAsync(sessionId, ctx.Arguments, ct);
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// MAF/MEAI 管道里工具返回值可能是 <see cref="string"/>、<see cref="JsonElement"/> 等，不能只用 <c>as string</c>，
    /// 否则 <c>plan_created</c> 等依赖全文结果的逻辑会静默跳过。
    /// </summary>
    internal static string? NormalizeToolResultToString(object? result)
    {
        if (result is null) return null;
        if (result is string s) return s;
        if (result is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => je.ToString()
            };
        }
        return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task TryEmitPlanCreatedAsync(string sessionId, string? fullResult)
    {
        if (string.IsNullOrEmpty(fullResult)) return;
        try
        {
            var planIdMatch = Regex.Match(fullResult, @"planId=([a-zA-Z0-9]+)");
            if (!planIdMatch.Success) return;
            var planId = planIdMatch.Groups[1].Value;
            var titleMatch = Regex.Match(fullResult, @"标题[：:]\s*([^。\n]+)");
            var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : planId;
            var createdBy = _sessionManager.GetClientType(sessionId);
            var msg = new WsMessage
            {
                Type = "plan_created",
                PlanId = planId,
                Title = string.IsNullOrEmpty(title) ? planId : title,
                Path = null,
                CreatedBy = createdBy
            };
            var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
            await _sessionManager.SendToAsync(sessionId, json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "plan_created emit failed");
        }
    }

    private async Task TryEmitPlanUpdatedAsync(string sessionId, IReadOnlyDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var planId = GetToolArgumentString(arguments, "planId", "PlanId");
        if (string.IsNullOrEmpty(planId)) return;
        try
        {
            string title = planId;
            var loaded = await _planStore.GetAsync(planId, ct).ConfigureAwait(false);
            if (loaded != null)
            {
                var t = loaded.Value.Meta.Title?.Trim();
                if (!string.IsNullOrEmpty(t))
                    title = t;
            }
            var createdBy = _sessionManager.GetClientType(sessionId);
            var msg = new WsMessage
            {
                Type = "plan_updated",
                PlanId = planId,
                Title = title,
                CreatedBy = createdBy
            };
            var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
            await _sessionManager.SendToAsync(sessionId, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "plan_updated emit failed");
        }
    }

    private static string? GetToolArgumentString(IReadOnlyDictionary<string, object?>? args, params string[] keys)
    {
        if (args == null) return null;
        foreach (var key in keys)
        {
            if (!args.TryGetValue(key, out var v) || v == null) continue;
            var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrEmpty(s))
                return s;
        }
        return null;
    }

    private static string? GetRunningDetail(string functionName, IDictionary<string, object?> arguments)
    {
        if (functionName == "run_command" && arguments.TryGetValue("command", out var cmdObj))
        {
            var cmd = cmdObj?.ToString()?.Trim();
            return string.IsNullOrEmpty(cmd) ? null : $"命令 «{LogPreview.HeadTail(cmd, 48, 48)}»";
        }
        if (string.Equals(functionName, "run_builtin_page_script", StringComparison.OrdinalIgnoreCase)
            && arguments.TryGetValue("scriptId", out var scriptObj))
        {
            var scriptId = scriptObj?.ToString()?.Trim();
            if (string.IsNullOrEmpty(scriptId)) return null;
            if (arguments.TryGetValue("paramsJson", out var paramsObj) && paramsObj is string paramsStr && !string.IsNullOrWhiteSpace(paramsStr) && paramsStr != "{}")
                return $"页面脚本 «{scriptId}» 参数: {LogPreview.HeadTail(paramsStr, 64, 64)}";
            return $"页面脚本 «{scriptId}»";
        }
        if (string.Equals(functionName, "run_custom_javascript_in_page", StringComparison.OrdinalIgnoreCase)
            && arguments.TryGetValue("scriptCode", out var customCodeObj))
        {
            var code = customCodeObj?.ToString()?.Trim() ?? "";
            return code.Length <= 80 ? $"自定义页面脚本: {code}" : $"自定义页面脚本: {code[..80]}...";
        }
        if (functionName == "current_run_custom_document_script" && arguments.TryGetValue("scriptCode", out var docCodeObj))
        {
            var code = docCodeObj?.ToString()?.Trim() ?? "";
            return code.Length <= 80 ? $"自定义文档脚本: {code}" : $"自定义文档脚本: {code[..80]}...";
        }
        return null;
    }

    private static int? GetPlanStepIndex(string pluginName, string functionName, IDictionary<string, object?> arguments)
    {
        if (!string.Equals(pluginName, "Plan", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(functionName, "execute_plan_step", StringComparison.OrdinalIgnoreCase))
            return null;
        if (arguments.TryGetValue("stepIndex", out var stepObj) && stepObj is int stepIndex and > 0)
            return stepIndex;
        return null;
    }

    private static string? GetSlowIoSummarySuffix(string pluginName, string functionName)
    {
        if (string.Equals(pluginName, "Word", StringComparison.OrdinalIgnoreCase)
            && string.Equals(functionName, "word_document_create", StringComparison.OrdinalIgnoreCase))
            return "（写入 Word 可能需数十秒，请稍候）";
        if (!string.Equals(pluginName, "Excel", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(functionName, "excel_range_write", StringComparison.OrdinalIgnoreCase))
            return "（写入 Excel 可能需数十秒，请稍候）";
        if (string.Equals(functionName, "excel_range_read", StringComparison.OrdinalIgnoreCase))
            return "（读取大表可能较慢，请稍候）";
        return null;
    }

    private static string? GetSlowIoAgentStatusLine(string pluginName, string functionName)
    {
        if (string.Equals(pluginName, "Word", StringComparison.OrdinalIgnoreCase)
            && string.Equals(functionName, "word_document_create", StringComparison.OrdinalIgnoreCase))
            return "正在写入 Word 文档，请稍候…";
        if (!string.Equals(pluginName, "Excel", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(functionName, "excel_range_write", StringComparison.OrdinalIgnoreCase))
            return "正在写入 Excel，请稍候…";
        if (string.Equals(functionName, "excel_range_read", StringComparison.OrdinalIgnoreCase))
            return "正在读取 Excel 数据，请稍候…";
        return null;
    }

    private static bool IsToolResultFailure(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (content.StartsWith("[MCP ", StringComparison.Ordinal)) return true;
        if (!content.StartsWith("[", StringComparison.Ordinal)) return false;
        return content.Contains("失败", StringComparison.Ordinal)
            || content.Contains("错误", StringComparison.Ordinal)
            || content.Contains("未启用", StringComparison.Ordinal)
            || content.Contains("无效", StringComparison.Ordinal)
            || content.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || content.Contains("系统拦截", StringComparison.Ordinal)
            || content.Contains("用户拒绝", StringComparison.Ordinal);
    }

    private async Task SendToolStatusAsync(
        string sessionId, string type, string plugin, string function,
        bool? success, string? content,
        string? startDetail = null, int? planStepIndex = null, string? slowIoSummarySuffix = null,
        CancellationToken cancellationToken = default)
    {
        var summary = type == "tool_invocation_start"
            ? (string.IsNullOrEmpty(startDetail) ? $"正在执行: {plugin}.{function}" : $"正在执行: {plugin}.{function} — {startDetail}")
            : null;
        if (summary != null && !string.IsNullOrEmpty(slowIoSummarySuffix))
            summary += " " + slowIoSummarySuffix;
        var msg = new WsMessage
        {
            Type = type,
            Plugin = plugin,
            Function = function,
            Success = success,
            Summary = summary,
            Content = content ?? "",
            PlanStepIndex = planStepIndex,
            IsSubtask = SubtaskContext.GetIsActive() ? true : null
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, json, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAgentPhaseAsync(string sessionId, string phase, string content, CancellationToken cancellationToken = default)
    {
        var msg = new WsMessage
        {
            Type = "agent_phase",
            Phase = phase,
            Content = content ?? ""
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, json, cancellationToken).ConfigureAwait(false);
    }
}
