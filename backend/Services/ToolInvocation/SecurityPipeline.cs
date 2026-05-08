using OpenWorkmate.Server.Logging;
using OpenWorkmate.Server.Services;

namespace OpenWorkmate.Server.Services.ToolInvocation;

/// <summary>
/// HITL / 白名单安全检查：run_command、run_builtin_page_script（扩展 RPC 与 HITL hitlKind 同名）、current_run_document_script、
/// run_custom_javascript_in_page、current_run_custom_document_script。
/// 从已删除的 SK <c>SecurityFilter</c> 迁入，接口为 <see cref="ISecurityPipeline"/>。
/// </summary>
public sealed class SecurityPipeline : ISecurityPipeline
{
    private readonly ILogger<SecurityPipeline> _logger;
    private readonly ConfigService _configService;
    private readonly HitlManager _hitlManager;
    private readonly SessionManager _sessionManager;
    private readonly IHitlPlainLanguageExplainer _hitlPlainLanguage;

    public SecurityPipeline(
        ILogger<SecurityPipeline> logger,
        ConfigService configService,
        HitlManager hitlManager,
        SessionManager sessionManager,
        IHitlPlainLanguageExplainer hitlPlainLanguage)
    {
        _logger = logger;
        _configService = configService;
        _hitlManager = hitlManager;
        _sessionManager = sessionManager;
        _hitlPlainLanguage = hitlPlainLanguage;
    }

    public async Task<string?> EvaluateAsync(
        string pluginName, string functionName,
        IDictionary<string, object?> arguments, CancellationToken ct)
    {
        var ruleEffect = ToolPermissionRuleEvaluator.Evaluate(
            _configService.Current.ToolPermissionRules, pluginName, functionName);

        if (functionName == "run_command" && arguments.TryGetValue("command", out var cmdObj))
            return await EvaluateRunCommandAsync(cmdObj, ruleEffect, ct).ConfigureAwait(false);

        if (string.Equals(functionName, "run_builtin_page_script", StringComparison.OrdinalIgnoreCase)
            && arguments.TryGetValue("scriptId", out var scriptIdObj))
            return await EvaluateRunPageScriptAsync(scriptIdObj, ruleEffect, ct).ConfigureAwait(false);

        if (functionName == "current_run_document_script" && arguments.TryGetValue("scriptId", out var docScriptIdObj))
            return await EvaluateDocumentScriptAsync(docScriptIdObj, ruleEffect, ct).ConfigureAwait(false);

        if (string.Equals(functionName, "run_custom_javascript_in_page", StringComparison.OrdinalIgnoreCase)
            && arguments.TryGetValue("scriptCode", out var scriptCodeObj))
            return await EvaluateCustomPageScriptAsync(scriptCodeObj, ruleEffect, ct).ConfigureAwait(false);

        if (functionName == "current_run_custom_document_script" && arguments.TryGetValue("scriptCode", out var docCodeObj))
            return await EvaluateCustomDocumentScriptAsync(docCodeObj, ruleEffect, ct).ConfigureAwait(false);

        _logger.LogInformation("Invoking tool: {Name}", functionName);
        return null; // allow
    }

    // ─── run_command ────────────────────────────────────────────────────

    private async Task<string?> EvaluateRunCommandAsync(object? cmdObj, ToolPermissionRuleEffect ruleEffect, CancellationToken ct)
    {
        var sessionId = SessionContext.GetSessionId();
        var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
        var endKey = IsScheduledTaskSession(sessionId) ? CliScriptEndKeys.Backend : CliScriptEndKeys.ResolveEndKey(clientType);
        var mode = _configService.GetCliRunModeForEnd(endKey);

        if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行命令 len={Len} preview={Command}",
                endKey, cmdObj?.ToString()?.Length ?? 0, LogPreview.HeadTail(cmdObj?.ToString(), 96, 96));
            return null;
        }

        var command = cmdObj?.ToString()?.Trim().ToLowerInvariant() ?? "";
        var cmdName = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var allowedCli = _configService.GetAllowedCliCommandsForEnd(endKey);
        var cliList = (allowedCli != null && allowedCli.Count > 0) ? allowedCli : CliScriptEndKeys.GetDefaultAllowedCliCommands(CliScriptEndKeys.Backend);
        var cliSet = cliList.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();

        if (IsScheduledTaskSession(sessionId))
        {
            if (cmdName != null && cliSet.Contains(cmdName))
                return null;
            return "[系统拦截] 定时任务到点执行无法弹出人工确认。请将命令加入设置「安全与确认 → 后台」的 CLI 白名单，或将后台运行模式设为 RunEverything。";
        }

        var needHitl = string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase)
            || (cmdName == null || !cliSet.Contains(cmdName));
        ToolPermissionRuleEvaluator.ApplyToNeedHitl(ref needHitl, ruleEffect);

        if (needHitl)
        {
            if (string.IsNullOrEmpty(sessionId))
                return "[系统拦截] 安全策略禁止执行该命令，且当前无会话无法进行人工确认。";
            var commandLine = cmdObj?.ToString()?.Trim() ?? "";
            var result = await RequestHitlWithPlainSummaryAsync(sessionId, commandLine, "run_command", cmdName, ct);
            if (!result.Allowed)
                return "用户拒绝执行或未在限定时间内确认，已取消执行。";
            _logger.LogInformation("用户已允许执行命令 len={Len} preview={Command}",
                commandLine.Length, LogPreview.HeadTail(commandLine, 96, 96));
        }
        return null;
    }

    // ─── run_builtin_page_script（Chrome 扩展 RPC 同名）──────────────────

    private async Task<string?> EvaluateRunPageScriptAsync(object? scriptIdObj, ToolPermissionRuleEffect ruleEffect, CancellationToken ct)
    {
        var sessionId = SessionContext.GetSessionId();
        var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
        var endKey = IsScheduledTaskSession(sessionId) ? CliScriptEndKeys.Backend : CliScriptEndKeys.ResolveEndKey(clientType);
        var mode = _configService.GetCliRunModeForEnd(endKey);

        if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行页面脚本 {ScriptId}", endKey, scriptIdObj?.ToString());
            return null;
        }

        var scriptId = scriptIdObj?.ToString()?.Trim();
        var allowedScripts = _configService.GetAllowedPageScriptIdsForEnd(endKey);
        var list = (allowedScripts != null && allowedScripts.Count > 0) ? allowedScripts : CliScriptEndKeys.DefaultAllowedScriptIds;
        var normalized = list.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.ToLowerInvariant()).ToHashSet();

        if (IsScheduledTaskSession(sessionId))
        {
            if (!string.IsNullOrEmpty(scriptId) && normalized.Contains(scriptId.ToLowerInvariant()))
                return null;
            return "[系统拦截] 定时任务到点执行无法弹出人工确认。请将脚本 id 加入设置「安全与确认 → Chrome」的页面脚本白名单，或将运行模式设为 RunEverything。";
        }

        var needHitl = string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(scriptId) || !normalized.Contains(scriptId.ToLowerInvariant()));
        ToolPermissionRuleEvaluator.ApplyToNeedHitl(ref needHitl, ruleEffect);

        if (needHitl)
        {
            if (string.IsNullOrEmpty(sessionId))
                return "[系统拦截] 安全策略禁止执行该页面脚本，且当前无会话无法进行人工确认。";
            var result = await RequestHitlWithPlainSummaryAsync(sessionId, scriptId ?? "", "run_builtin_page_script", scriptId ?? "", ct);
            if (!result.Allowed)
                return "用户拒绝执行或未在限定时间内确认，已取消执行。";
            _logger.LogInformation("用户已允许执行页面脚本: {ScriptId}", scriptId);
        }
        return null;
    }

    // ─── current_run_document_script ────────────────────────────────────

    private async Task<string?> EvaluateDocumentScriptAsync(object? docScriptIdObj, ToolPermissionRuleEffect ruleEffect, CancellationToken ct)
    {
        var sessionId = SessionContext.GetSessionId();
        if (IsScheduledTaskSession(sessionId))
            return "[系统拦截] 定时任务到点执行不支持 current_run_document_script（无文档宿主会话）。";

        var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
        var endKey = CliScriptEndKeys.ResolveEndKey(clientType);
        if (!string.Equals(endKey, CliScriptEndKeys.Office, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endKey, CliScriptEndKeys.Wps, StringComparison.OrdinalIgnoreCase))
            return null;

        var mode = _configService.GetCliRunModeForEnd(endKey);
        if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行文档脚本 {ScriptId}", endKey, docScriptIdObj?.ToString());
            return null;
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
                return "[系统拦截] 安全策略禁止执行该文档脚本，且当前无会话无法进行人工确认。";
            var result = await RequestHitlWithPlainSummaryAsync(sessionId, scriptId ?? "", "current_run_document_script", scriptId ?? "", ct);
            if (!result.Allowed)
                return "用户拒绝执行或未在限定时间内确认，已取消执行。";
            _logger.LogInformation("用户已允许执行文档脚本: {ScriptId}", scriptId);
        }
        return null;
    }

    // ─── run_custom_javascript_in_page（Chrome 扩展 RPC 同名）───────────

    private async Task<string?> EvaluateCustomPageScriptAsync(object? scriptCodeObj, ToolPermissionRuleEffect ruleEffect, CancellationToken ct)
    {
        var sessionId = SessionContext.GetSessionId();
        if (IsScheduledTaskSession(sessionId))
            return "[系统拦截] 定时任务到点执行不支持自定义页面脚本（需人工确认且无浏览器会话）。";

        var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
        var endKey = CliScriptEndKeys.ResolveEndKey(clientType);
        var mode = _configService.GetCliRunModeForEnd(endKey);

        if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行自定义页面脚本", endKey);
            return null;
        }

        if (!ToolPermissionRuleEvaluator.RequiresConfirmation(ruleEffect))
            return null;

        if (string.IsNullOrEmpty(sessionId))
            return "[系统拦截] 执行自定义页面脚本需在会话中由用户确认，当前无会话。";
        var code = scriptCodeObj?.ToString()?.Trim() ?? "";
        var result = await RequestHitlWithPlainSummaryAsync(sessionId, code, "run_custom_javascript_in_page", null, ct);
        if (!result.Allowed)
            return "用户拒绝执行或未在限定时间内确认，已取消执行。";
        _logger.LogInformation("用户已允许执行自定义页面脚本");
        return null;
    }

    // ─── current_run_custom_document_script ─────────────────────────────

    private async Task<string?> EvaluateCustomDocumentScriptAsync(object? docCodeObj, ToolPermissionRuleEffect ruleEffect, CancellationToken ct)
    {
        var sessionId = SessionContext.GetSessionId();
        if (IsScheduledTaskSession(sessionId))
            return "[系统拦截] 定时任务到点执行不支持自定义文档脚本（需人工确认）。";

        var clientType = !string.IsNullOrEmpty(sessionId) ? _sessionManager.GetClientType(sessionId) : null;
        var endKey = CliScriptEndKeys.ResolveEndKey(clientType);
        var mode = _configService.GetCliRunModeForEnd(endKey);

        if (string.Equals(mode, "RunEverything", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("RunEverything 模式（端={EndKey}）：放行自定义文档脚本", endKey);
            return null;
        }

        if (!ToolPermissionRuleEvaluator.RequiresConfirmation(ruleEffect))
            return null;

        if (string.IsNullOrEmpty(sessionId))
            return "[系统拦截] 执行自定义文档脚本需在会话中由用户确认，当前无会话。";
        var code = docCodeObj?.ToString()?.Trim() ?? "";
        var result = await RequestHitlWithPlainSummaryAsync(sessionId, code, "run_custom_document_script", null, ct);
        if (!result.Allowed)
            return "用户拒绝执行或未在限定时间内确认，已取消执行。";
        _logger.LogInformation("用户已允许执行自定义文档脚本");
        return null;
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private async Task<HitlResult> RequestHitlWithPlainSummaryAsync(
        string sessionId, string rawExecutableForDisplay, string hitlKind, string? addToAllowListKey, CancellationToken ct)
    {
        var raw = HitlPlainLanguageExplainer.TruncateRawExecutable(rawExecutableForDisplay);
        string? summary = null;
        if (raw.Length > 0)
        {
            _logger.LogDebug("HITL plain summary kind={Kind} rawLen={Len}", hitlKind, raw.Length);
            summary = await _hitlPlainLanguage.SummarizeAsync(raw, ct).ConfigureAwait(false);
        }
        return await _hitlManager.RequestConfirmationAsync(sessionId, raw, hitlKind, addToAllowListKey, summary, ct).ConfigureAwait(false);
    }

    private static bool IsScheduledTaskSession(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && sessionId.StartsWith("scheduled:", StringComparison.OrdinalIgnoreCase);
}
