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
    private readonly SessionManager _sessionManager;

    public SecurityFilter(ILogger<SecurityFilter> logger, ConfigService configService, HitlManager hitlManager, SessionManager sessionManager)
    {
        _logger = logger;
        _configService = configService;
        _hitlManager = hitlManager;
        _sessionManager = sessionManager;
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var functionName = context.Function.Name;

        if (functionName == "run_command" && context.Arguments.TryGetValue("command", out var cmdObj))
        {
            var sessionId = SessionContext.GetSessionId();
            var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
            var endKey = CliScriptEndKeys.ResolveEndKey(clientType);
            var mode = _configService.GetCliRunModeForEnd(endKey);

            if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行命令 {Command}", endKey, cmdObj?.ToString());
                await next(context);
                return;
            }

            var command = cmdObj?.ToString()?.Trim().ToLowerInvariant() ?? "";
            var cmdName = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var allowedCli = _configService.GetAllowedCliCommandsForEnd(endKey);
            var cliList = (allowedCli != null && allowedCli.Count > 0) ? allowedCli : CliScriptEndKeys.DefaultAllowedCommands;
            var cliSet = cliList.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();

            var needHitl = string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase)
                || (cmdName == null || !cliSet.Contains(cmdName));

            if (needHitl)
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Result = new FunctionResult(context.Function,
                        "[系统拦截] 安全策略禁止执行该命令，且当前无会话无法进行人工确认。");
                    return;
                }
                var action = "执行命令: " + (cmdObj?.ToString()?.Trim() ?? command);
                var result = await _hitlManager.RequestConfirmationAsync(sessionId, action, "run_command", cmdName);
                if (!result.Allowed)
                {
                    context.Result = new FunctionResult(context.Function,
                        "用户拒绝执行或未在限定时间内确认，已取消执行。");
                    return;
                }
                _logger.LogInformation("用户已允许执行命令: {Command}", command);
            }

            await next(context);
            return;
        }

        if (functionName == "run_page_script" && context.Arguments.TryGetValue("scriptId", out var scriptIdObj))
        {
            var sessionId = SessionContext.GetSessionId();
            var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
            var endKey = CliScriptEndKeys.ResolveEndKey(clientType);
            var mode = _configService.GetCliRunModeForEnd(endKey);

            if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行页面脚本 {ScriptId}", endKey, scriptIdObj?.ToString());
                await next(context);
                return;
            }

            var scriptId = scriptIdObj?.ToString()?.Trim();
            var allowedScripts = _configService.GetAllowedPageScriptIdsForEnd(endKey);
            var list = (allowedScripts != null && allowedScripts.Count > 0) ? allowedScripts : CliScriptEndKeys.DefaultAllowedScriptIds;
            var normalized = list.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();

            var needHitl = string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(scriptId) || !normalized.Contains(scriptId.ToLowerInvariant()));

            if (needHitl)
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Result = new FunctionResult(context.Function,
                        "[系统拦截] 安全策略禁止执行该页面脚本，且当前无会话无法进行人工确认。");
                    return;
                }
                var action = "执行页面脚本: " + (scriptId ?? "");
                var result = await _hitlManager.RequestConfirmationAsync(sessionId, action, "run_page_script", scriptId ?? "");
                if (!result.Allowed)
                {
                    context.Result = new FunctionResult(context.Function,
                        "用户拒绝执行或未在限定时间内确认，已取消执行。");
                    return;
                }
                _logger.LogInformation("用户已允许执行页面脚本: {ScriptId}", scriptId);
            }

            await next(context);
            return;
        }

        _logger.LogInformation("Invoking tool: {Name}", functionName);
        await next(context);
    }
}
