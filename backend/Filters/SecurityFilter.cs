using System.Collections.Generic;
using System.Linq;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Filters;

public sealed class SecurityFilter : IFunctionInvocationFilter
{
    private readonly ILogger<SecurityFilter> _logger;
    private readonly ConfigService _configService;
    private readonly HitlManager _hitlManager;

    private static readonly string[] DefaultAllowedCommands = {
        "dir", "echo", "type", "ping", "systeminfo", "ipconfig"
    };

    private static readonly string[] DefaultAllowedScriptIds = {
        "scroll_to_top", "scroll_to_bottom", "get_visible_text", "get_page_title"
    };

    public SecurityFilter(ILogger<SecurityFilter> logger, ConfigService configService, HitlManager hitlManager)
    {
        _logger = logger;
        _configService = configService;
        _hitlManager = hitlManager;
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var functionName = context.Function.Name;

        if (functionName == "run_command" && context.Arguments.TryGetValue("command", out var cmdObj))
        {
            if (_configService.Current.RunEverythingMode)
            {
                _logger.LogInformation("RunEverything 模式：放行命令 {Command}", cmdObj?.ToString());
                await next(context);
                return;
            }
            var command = cmdObj?.ToString()?.Trim().ToLowerInvariant() ?? "";
            var cmdName = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var allowedCli = _configService.Current.AllowedCliCommands;
            var cliList = (allowedCli != null && allowedCli.Count > 0) ? (IEnumerable<string>)allowedCli : DefaultAllowedCommands;
            var cliSet = cliList.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();
            if (cmdName == null || !cliSet.Contains(cmdName))
            {
                _logger.LogWarning("拦截到危险命令，发起 HITL 确认: {Command}", command);
                var sessionId = SessionContext.GetSessionId();
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Result = new FunctionResult(context.Function,
                        "[系统拦截] 安全策略禁止执行该命令，且当前无会话无法进行人工确认。");
                    return;
                }
                var action = "执行命令: " + (cmdObj?.ToString()?.Trim() ?? command);
                var allowed = await _hitlManager.RequestConfirmationAsync(sessionId, action);
                if (!allowed)
                {
                    context.Result = new FunctionResult(context.Function,
                        "用户拒绝执行或未在限定时间内确认，已取消执行。");
                    return;
                }
                _logger.LogInformation("用户已允许执行命令: {Command}", command);
                await next(context);
                return;
            }
        }

        if (functionName == "run_page_script" && context.Arguments.TryGetValue("scriptId", out var scriptIdObj))
        {
            if (_configService.Current.RunEverythingMode)
            {
                _logger.LogInformation("RunEverything 模式：放行页面脚本 {ScriptId}", scriptIdObj?.ToString());
                await next(context);
                return;
            }
            var scriptId = scriptIdObj?.ToString()?.Trim();
            var allowed = _configService.Current.AllowedPageScriptIds;
            var list = (allowed != null && allowed.Count > 0) ? (IEnumerable<string>)allowed : DefaultAllowedScriptIds;
            var normalized = list.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();
            if (string.IsNullOrEmpty(scriptId) || !normalized.Contains(scriptId.ToLowerInvariant()))
            {
                _logger.LogWarning("拦截到未授权页面脚本，发起 HITL 确认: {ScriptId}", scriptId);
                var sessionId = SessionContext.GetSessionId();
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Result = new FunctionResult(context.Function,
                        "[系统拦截] 安全策略禁止执行该页面脚本，且当前无会话无法进行人工确认。");
                    return;
                }
                var action = "执行页面脚本: " + (scriptId ?? "");
                var userAllowed = await _hitlManager.RequestConfirmationAsync(sessionId, action);
                if (!userAllowed)
                {
                    context.Result = new FunctionResult(context.Function,
                        "用户拒绝执行或未在限定时间内确认，已取消执行。");
                    return;
                }
                _logger.LogInformation("用户已允许执行页面脚本: {ScriptId}", scriptId);
                await next(context);
                return;
            }
        }

        _logger.LogInformation("Invoking tool: {Name}", functionName);
        await next(context);
    }
}
