using System.Collections.Generic;
using System.Linq;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Filters;

/// <summary>
/// 拦截需人工确认（HITL）或白名单策略的高危函数调用（任意 shell、任意页面/文档脚本代码）。
/// <para><b>本过滤器处理的函数名</b>：<c>run_command</c>、<c>run_page_script</c>、<c>run_custom_page_script</c>、<c>current_run_document_script</c>、<c>current_run_custom_document_script</c>。</para>
/// <para><b>有意不纳入</b>：Office/Word/Excel/PPT 等其它文档编辑类工具；<c>run_clawhub_script</c>（仅技能包 scripts/ 下已存在脚本）。新增可执行任意用户输入或系统命令的工具时，应在此增加分支或更新本说明。</para>
/// <para><b>定时任务会话</b>（<c>sessionId</c> 前缀 <c>scheduled:</c>）：无前端，无法弹 HITL；CLI/页面脚本/自定义文档脚本分支为白名单放行或拒绝并返回明确 <c>[系统拦截]</c> 文案，不静默越权执行。</para>
/// </summary>
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
        var pluginName = context.Function.Metadata?.PluginName ?? context.Function.PluginName ?? "";
        var functionName = context.Function.Name ?? "";
        var ruleEffect = ToolPermissionRuleEvaluator.Evaluate(_configService.Current.ToolPermissionRules, pluginName, functionName);
        if (ruleEffect == ToolPermissionRuleEffect.Deny)
        {
            context.Result = new FunctionResult(context.Function,
                "[系统拦截] 工具权限规则禁止调用 " + pluginName + "." + functionName + "。");
            return;
        }

        if (functionName == "run_command" && context.Arguments.TryGetValue("command", out var cmdObj))
        {
            var sessionId = SessionContext.GetSessionId();
            var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
            var endKey = IsScheduledTaskSession(sessionId) ? CliScriptEndKeys.Backend : CliScriptEndKeys.ResolveEndKey(clientType);
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
            var cliList = (allowedCli != null && allowedCli.Count > 0) ? allowedCli : CliScriptEndKeys.GetDefaultAllowedCliCommands(CliScriptEndKeys.Backend);
            var cliSet = cliList.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();

            // 定时任务到点执行：无前端，不弹 HITL；AskEverytime 降级为仅白名单放行（与 UseAllowList 行为一致）。
            if (IsScheduledTaskSession(sessionId))
            {
                if (cmdName != null && cliSet.Contains(cmdName))
                {
                    await next(context);
                    return;
                }
                context.Result = new FunctionResult(context.Function,
                    "[系统拦截] 定时任务到点执行无法弹出人工确认。请将命令加入设置「安全与确认 → 后台」的 CLI 白名单，或将后台运行模式设为 RunEverything。");
                return;
            }

            var needHitl = string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase)
                || (cmdName == null || !cliSet.Contains(cmdName));
            ToolPermissionRuleEvaluator.ApplyToNeedHitl(ref needHitl, ruleEffect);

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
            var endKey = IsScheduledTaskSession(sessionId) ? CliScriptEndKeys.Backend : CliScriptEndKeys.ResolveEndKey(clientType);
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

            if (IsScheduledTaskSession(sessionId))
            {
                if (!string.IsNullOrEmpty(scriptId) && normalized.Contains(scriptId.ToLowerInvariant()))
                {
                    await next(context);
                    return;
                }
                context.Result = new FunctionResult(context.Function,
                    "[系统拦截] 定时任务到点执行无法弹出人工确认。请将脚本 id 加入设置「安全与确认 → Chrome」的页面脚本白名单，或将运行模式设为 RunEverything。");
                return;
            }

            var needHitl = string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(scriptId) || !normalized.Contains(scriptId.ToLowerInvariant()));
            ToolPermissionRuleEvaluator.ApplyToNeedHitl(ref needHitl, ruleEffect);

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

        if (functionName == "current_run_document_script" && context.Arguments.TryGetValue("scriptId", out var docScriptIdObj))
        {
            var sessionId = SessionContext.GetSessionId();
            if (IsScheduledTaskSession(sessionId))
            {
                context.Result = new FunctionResult(context.Function,
                    "[系统拦截] 定时任务到点执行不支持 current_run_document_script（无文档宿主会话）。");
                return;
            }

            var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
            var endKey = CliScriptEndKeys.ResolveEndKey(clientType);
            if (!string.Equals(endKey, CliScriptEndKeys.Office, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(endKey, CliScriptEndKeys.Wps, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var mode = _configService.GetCliRunModeForEnd(endKey);

            if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行文档脚本 {ScriptId}", endKey, docScriptIdObj?.ToString());
                await next(context);
                return;
            }

            var scriptId = docScriptIdObj?.ToString()?.Trim();
            var allowedDoc = _configService.GetAllowedDocumentScriptIdsForEnd(endKey);
            var docList = (allowedDoc != null && allowedDoc.Count > 0) ? allowedDoc : CliScriptEndKeys.GetDefaultAllowedDocumentScriptIds(endKey);
            var docSet = docList.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();

            var needHitl = string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(scriptId) || !docSet.Contains(scriptId.ToLowerInvariant()));
            ToolPermissionRuleEvaluator.ApplyToNeedHitl(ref needHitl, ruleEffect);

            if (needHitl)
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Result = new FunctionResult(context.Function,
                        "[系统拦截] 安全策略禁止执行该文档脚本，且当前无会话无法进行人工确认。");
                    return;
                }
                var action = "执行文档预定义脚本: " + (scriptId ?? "");
                var result = await _hitlManager.RequestConfirmationAsync(sessionId, action, "current_run_document_script", scriptId ?? "");
                if (!result.Allowed)
                {
                    context.Result = new FunctionResult(context.Function,
                        "用户拒绝执行或未在限定时间内确认，已取消执行。");
                    return;
                }
                _logger.LogInformation("用户已允许执行文档脚本: {ScriptId}", scriptId);
            }

            await next(context);
            return;
        }

        if (functionName == "run_custom_page_script" && context.Arguments.TryGetValue("scriptCode", out var scriptCodeObj))
        {
            var sessionId = SessionContext.GetSessionId();
            if (IsScheduledTaskSession(sessionId))
            {
                context.Result = new FunctionResult(context.Function,
                    "[系统拦截] 定时任务到点执行不支持自定义页面脚本（需人工确认且无浏览器会话）。");
                return;
            }
            if (ToolPermissionRuleEvaluator.RequiresConfirmation(ruleEffect))
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Result = new FunctionResult(context.Function,
                        "[系统拦截] 执行自定义页面脚本需在会话中由用户确认，当前无会话。");
                    return;
                }
                var code = scriptCodeObj?.ToString()?.Trim() ?? "";
                const int PreviewLen = 200;
                var action = "执行自定义页面脚本: " + (code.Length <= PreviewLen ? code : code.Substring(0, PreviewLen) + "...");
                var result = await _hitlManager.RequestConfirmationAsync(sessionId, action, "run_custom_page_script", null);
                if (!result.Allowed)
                {
                    context.Result = new FunctionResult(context.Function,
                        "用户拒绝执行或未在限定时间内确认，已取消执行。");
                    return;
                }
                _logger.LogInformation("用户已允许执行自定义页面脚本");
            }
            await next(context);
            return;
        }

        if (functionName == "current_run_custom_document_script" && context.Arguments.TryGetValue("scriptCode", out var docScriptCodeObj))
        {
            var sessionId = SessionContext.GetSessionId();
            if (IsScheduledTaskSession(sessionId))
            {
                context.Result = new FunctionResult(context.Function,
                    "[系统拦截] 定时任务到点执行不支持自定义文档脚本（需人工确认）。");
                return;
            }
            if (ToolPermissionRuleEvaluator.RequiresConfirmation(ruleEffect))
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Result = new FunctionResult(context.Function,
                        "[系统拦截] 执行自定义文档脚本需在会话中由用户确认，当前无会话。");
                    return;
                }
                var code = docScriptCodeObj?.ToString()?.Trim() ?? "";
                const int PreviewLen = 200;
                var action = "执行自定义文档脚本: " + (code.Length <= PreviewLen ? code : code.Substring(0, PreviewLen) + "...");
                var result = await _hitlManager.RequestConfirmationAsync(sessionId, action, "run_custom_document_script", null);
                if (!result.Allowed)
                {
                    context.Result = new FunctionResult(context.Function,
                        "用户拒绝执行或未在限定时间内确认，已取消执行。");
                    return;
                }
                _logger.LogInformation("用户已允许执行自定义文档脚本");
            }
            await next(context);
            return;
        }

        _logger.LogInformation("Invoking tool: {Name}", functionName);
        await next(context);
    }

    /// <summary>定时任务 Runner 使用的会话 id 前缀；无 WebSocket，不按前台端走 HITL。</summary>
    private static bool IsScheduledTaskSession(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && sessionId.StartsWith("scheduled:", StringComparison.OrdinalIgnoreCase);
}
