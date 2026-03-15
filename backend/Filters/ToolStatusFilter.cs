using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Filters;

/// <summary>
/// 在每次 Kernel 函数调用前后向当前会话推送 tool_invocation_start / tool_invocation_end，
/// 便于前端展示「正在执行」「执行完成」状态；失败时使用友好文案。
/// </summary>
public sealed class ToolStatusFilter : IFunctionInvocationFilter
{
    private readonly SessionManager _sessionManager;
    private readonly ILogger<ToolStatusFilter> _logger;

    public ToolStatusFilter(SessionManager sessionManager, ILogger<ToolStatusFilter> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var sessionId = SessionContext.GetSessionId();
        var pluginName = context.Function.Metadata?.PluginName ?? context.Function.PluginName ?? "?";
        var functionName = context.Function.Name ?? "?";

        if (string.IsNullOrEmpty(sessionId))
        {
            await next(context);
            return;
        }

        // 对 run_command / run_page_script 提取正在执行的命令或脚本，便于前端展示
        var startDetail = GetRunningDetail(functionName, context.Arguments);
        var planStepIndex = GetPlanStepIndex(pluginName, functionName, context.Arguments);

        // 发送开始
        await SendToolStatusAsync(sessionId, "tool_invocation_start", pluginName, functionName, null, null, startDetail, planStepIndex);

        try
        {
            await next(context);

            // 正常返回：发送成功结束；可选附带简短结果摘要；若返回串表示错误则按失败下发
            var content = "";
            try
            {
                var s = context.Result?.GetValue<string>();
                if (!string.IsNullOrEmpty(s))
                    content = s.Length <= 200 ? s : s.Substring(0, 200);
            }
            catch { /* 非字符串结果忽略 */ }

            var success = !IsToolResultFailure(content);
            await SendToolStatusAsync(sessionId, "tool_invocation_end", pluginName, functionName, success, content, null, planStepIndex);

            // Plan.create_plan 成功后推送 plan_created，便于前端打开计划页（仅当工具结果视为成功时）
            if (success && string.Equals(pluginName, "Plan", StringComparison.OrdinalIgnoreCase) && string.Equals(functionName, "create_plan", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fullResult = context.Result?.GetValue<string>() ?? "";
                    var planIdMatch = System.Text.RegularExpressions.Regex.Match(fullResult, @"planId=([a-zA-Z0-9]+)");
                    var titleMatch = System.Text.RegularExpressions.Regex.Match(fullResult, @"标题[：:]\s*([^。\n]+)");
                    if (planIdMatch.Success)
                    {
                        var createdBy = _sessionManager.GetClientType(sessionId);
                        var requiresConfirm = fullResult.Contains("需用户确认", StringComparison.Ordinal);
                        var planCreated = new WsMessage
                        {
                            Type = "plan_created",
                            PlanId = planIdMatch.Groups[1].Value,
                            Title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : null,
                            Path = null,
                            CreatedBy = createdBy,
                            RequiresUserConfirmation = requiresConfirm
                        };
                        var json = JsonSerializer.Serialize(planCreated, JsonCtx.Default.WsMessage);
                        await _sessionManager.SendToAsync(sessionId, json);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "plan_created emit failed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ToolStatus] Function {Plugin}.{Function} failed", pluginName, functionName);
            var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
            var stepIndexOnFail = GetPlanStepIndex(pluginName, functionName, context.Arguments);
            await SendToolStatusAsync(sessionId, "tool_invocation_end", pluginName, functionName, false, friendly, null, stepIndexOnFail);
            throw;
        }
    }

    private static int? GetPlanStepIndex(string pluginName, string functionName, KernelArguments? arguments)
    {
        if (arguments == null) return null;
        if (!string.Equals(pluginName, "Plan", StringComparison.OrdinalIgnoreCase) || !string.Equals(functionName, "execute_plan_step", StringComparison.OrdinalIgnoreCase))
            return null;
        if (arguments.TryGetValue("stepIndex", out var stepObj) && stepObj is int stepIndex and > 0)
            return stepIndex;
        return null;
    }

    private static string? GetRunningDetail(string functionName, KernelArguments arguments)
    {
        if (arguments == null) return null;
        if (functionName == "run_command" && arguments.TryGetValue("command", out var cmdObj))
        {
            var cmd = cmdObj?.ToString()?.Trim();
            return string.IsNullOrEmpty(cmd) ? null : $"命令 «{cmd}»";
        }
        if (functionName == "run_page_script" && arguments.TryGetValue("scriptId", out var scriptObj))
        {
            var scriptId = scriptObj?.ToString()?.Trim();
            if (string.IsNullOrEmpty(scriptId)) return null;
            if (arguments.TryGetValue("paramsJson", out var paramsObj) && paramsObj is string paramsStr && !string.IsNullOrWhiteSpace(paramsStr) && paramsStr != "{}")
                return $"页面脚本 «{scriptId}» 参数: {paramsStr}";
            return $"页面脚本 «{scriptId}»";
        }
        return null;
    }

    /// <summary>根据工具返回的字符串判断是否为“错误类”结果（插件/MCP/SecurityFilter 约定以 [xxx] 或关键词表示失败）。</summary>
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
        string sessionId,
        string type,
        string plugin,
        string function,
        bool? success,
        string? content,
        string? startDetail = null,
        int? planStepIndex = null)
    {
        var summary = type == "tool_invocation_start"
            ? (string.IsNullOrEmpty(startDetail) ? $"正在执行: {plugin}.{function}" : $"正在执行: {plugin}.{function} — {startDetail}")
            : null;
        var msg = new WsMessage
        {
            Type = type,
            Plugin = plugin,
            Function = function,
            Success = success,
            Summary = summary,
            Content = content ?? "",
            PlanStepIndex = planStepIndex
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, json);
    }
}
