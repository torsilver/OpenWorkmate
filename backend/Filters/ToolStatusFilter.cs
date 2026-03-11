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

        // 发送开始
        await SendToolStatusAsync(sessionId, "tool_invocation_start", pluginName, functionName, null, null);

        try
        {
            await next(context);

            // 正常返回：发送成功结束；可选附带简短结果摘要
            var content = "";
            try
            {
                var s = context.Result?.GetValue<string>();
                if (!string.IsNullOrEmpty(s) && s.Length <= 200)
                    content = s;
            }
            catch { /* 非字符串结果忽略 */ }

            await SendToolStatusAsync(sessionId, "tool_invocation_end", pluginName, functionName, true, content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ToolStatus] Function {Plugin}.{Function} failed", pluginName, functionName);
            var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
            await SendToolStatusAsync(sessionId, "tool_invocation_end", pluginName, functionName, false, friendly);
            throw;
        }
    }

    private async Task SendToolStatusAsync(
        string sessionId,
        string type,
        string plugin,
        string function,
        bool? success,
        string? content)
    {
        var msg = new WsMessage
        {
            Type = type,
            Plugin = plugin,
            Function = function,
            Success = success,
            Summary = type == "tool_invocation_start" ? $"正在执行: {plugin}.{function}" : null,
            Content = content ?? ""
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await _sessionManager.SendToAsync(sessionId, json);
    }
}
