using System.ComponentModel;
using System.Globalization;
using System.Text;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using OfficeCopilot.Server.Services.ToolInvocation;

namespace OfficeCopilot.Server.Plugins;

/// <summary>动态工具引导：检索允许列表内的工具元数据并激活，供主会话扩容 <see cref="Microsoft.Extensions.AI.ChatOptions.Tools"/>。</summary>
[CopilotPluginId("AgentTooling")]
public sealed class AgentToolingPlugin
{
    private readonly IChatRuntimeAccessor _runtime;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<AgentToolingPlugin> _logger;

    public AgentToolingPlugin(
        IChatRuntimeAccessor runtime,
        SessionManager sessionManager,
        ILogger<AgentToolingPlugin> logger)
    {
        _runtime = runtime;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    [ToolFunction(DynamicToolingConstants.SearchFunctionName)]
    [Description(
        "在「当前客户端允许」的工具列表中按关键词检索。每行以 OpenAPI/工具协议中的裸函数名为准，括号内为插件 Id。"
        + "发起 tool_calls 时必须使用裸函数名（与 schema 中 name 一致），勿使用 Plugin.function 形式。"
        + "若 system 列出已启用用户技能且任务可能依赖规范/文风/流程，建议先按需完成技能链（search_available_skills 等）再调用本工具；否则可直接检索。须 activate_tools 后再以裸名调用业务工具。")]
    public Task<string> SearchAvailableToolsAsync(
        [Description("检索关键词，可多个词；应尽量具体（空串时仍会返回若干项，含本轮引导工具优先）")] string query,
        [Description("最多返回条数，默认 8")] int topK = 8,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var state = DynamicToolingTurnScope.Current;
        if (state == null)
            return Task.FromResult("[search_available_tools] 当前不在动态工具模式，忽略。");

        if (state.SearchInvocationCount >= state.Config.MaxSearchPerTurn)
            return Task.FromResult($"[search_available_tools] 已达本轮检索上限（{state.Config.MaxSearchPerTurn}），请直接 activate_tools 或调用已激活工具。");

        state.SearchInvocationCount++;
        var k = Math.Clamp(topK, 1, 32);
        var hits = state.Catalog.Search(query, k, state.BootstrapFunctionNames);
        if (hits.Count == 0)
            return Task.FromResult("[search_available_tools] 无匹配项；可换关键词或检查任务是否需其它能力。");

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[search_available_tools] 共 {hits.Count} 条（query={(query ?? "").Trim()}）:\n");
        var i = 1;
        foreach (var e in hits)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{i}. {e.FunctionName}（插件 {e.PluginName}）");
            if (!string.IsNullOrWhiteSpace(e.ShortDescription))
                sb.Append(" — ").Append(e.ShortDescription.Trim());
            sb.Append('\n');
            i++;
        }
        sb.Append("activate_tools 可传裸函数名或 Plugin.function（用于消歧）；实际 tool_calls 仅认裸名，例如 [\"excel_range_read\",\"run_builtin_page_script\"]。");
        var collisions = _runtime.ToolRegistry.GetBareFunctionNameCollisions();
        if (collisions.Count > 0)
        {
            sb.Append("\n【同名提醒】以下裸函数名对应多个插件；若歧义请用 Plugin.function 调用或在 activate_tools 中传 Plugin.function：\n");
            foreach (var (fn, plugins) in collisions)
                sb.Append(" - ").Append(fn).Append(": ").Append(string.Join(", ", plugins)).Append('\n');
        }

        var topFnPreview = string.Join(", ", hits.Take(12).Select(e => e.FunctionName));
        if (hits.Count > 12)
            topFnPreview += ",…";
        _logger.LogInformation(
            "[search_available_tools] query={Query} hitCount={Count} topFunctionNames={TopNames} (返回行里为 裸函数名（插件 Id）；tool_calls 须用裸名)",
            (query ?? "").Trim(),
            hits.Count,
            topFnPreview);
        return Task.FromResult(sb.ToString());
    }

    [ToolFunction(DynamicToolingConstants.ActivateFunctionName)]
    [Description(
        "将业务工具加入本轮「已激活」集合（与 search_available_tools 配合）。可传裸函数名或 Plugin.function；toolNames 为数组，支持一次激活多个工具，建议把本轮需要的业务工具尽量放在同一次调用中，避免只激活一部分导致后续 tool_calls 报 Function not found。"
        + " 推荐在首次 search_available_tools 之前已按需走通技能链（见 system「动态工具」）。"
        + " 固定协议：若本轮已调用过 search_available_tools 且当前存在已启用的用户技能，须先至少调用一次 search_available_skills，再调用本工具；无启用技能或未使用过工具检索时不受此限。"
        + "注意：之后发起 tool_calls 时名称必须与 OpenAPI 工具 schema 一致（裸函数名）。示例：[\"excel_range_read\",\"Browser.run_builtin_page_script\"]。")]
    public Task<string> ActivateToolsAsync(
        [Description("要激活的工具函数名列表（裸函数名或 Plugin.function）")] string[] toolNames,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var state = DynamicToolingTurnScope.Current;
        if (state == null)
            return Task.FromResult("[activate_tools] 当前不在动态工具模式，忽略。");

        if (toolNames == null || toolNames.Length == 0)
            return Task.FromResult("[activate_tools] toolNames 为空，请传入至少一个函数名。");

        if (state.ActivateInvocationCount >= state.Config.MaxActivatePerTurn)
            return Task.FromResult($"[activate_tools] 已达本轮激活次数上限（{state.Config.MaxActivatePerTurn}）。");

        if (DynamicToolingActivateSkillGate.ShouldBlock(state, out var skillGateMsg))
            return Task.FromResult(skillGateMsg);

        state.ActivateInvocationCount++;
        var registry = _runtime.ToolRegistry;
        var sessionId = SessionContext.GetSessionId();
        var clientType = string.IsNullOrEmpty(sessionId) ? null : _sessionManager.GetClientType(sessionId);
        var wpsHostKindForActivate = string.Equals(clientType, "wps", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sessionId)
            ? (DynamicToolingTurnScope.Current?.WpsHostKindForTools ?? _sessionManager.GetWpsHostKind(sessionId))
            : null;

        var preview = string.Join(", ", toolNames.Take(12).Select(n =>
        {
            var t = n ?? "";
            return t.Length > 48 ? t[..48] + "…" : t;
        }));
        if (toolNames.Length > 12) preview += ",…";
        var inputsWithDot = toolNames.Count(n => (n ?? "").Contains('.', StringComparison.Ordinal));
        _logger.LogInformation(
            "[activate_tools] inputCount={Count} inputsContainingDot={QualifiedStyleCount} preview={Preview} (含点号多为 Plugin.function，仅用于激活；后续 tool_calls 仍须裸函数名)",
            toolNames.Length,
            inputsWithDot,
            preview);

        var activated = new List<string>();
        var rejected = new List<string>();
        var alreadyActive = new List<string>();
        var skippedEmpty = 0;
        var skippedMeta = 0;

        foreach (var raw in toolNames)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0)
            {
                skippedEmpty++;
                continue;
            }

            if (DynamicToolingConstants.MetaFunctionNames.Contains(s))
            {
                skippedMeta++;
                continue;
            }

            if (!ToolQualifiedNameResolver.TryResolve(registry, s, out var plugin, out var func, out _))
            {
                _logger.LogInformation(
                    "[activate_tools] reject input={Input} reason=not_in_registry_or_invalid_format (须为检索结果中的裸函数名，或唯一一段 Plugin.function)",
                    s);
                rejected.Add(s);
                continue;
            }

            if (!ClientTypeToolFilter.IsAllowed(plugin, func, clientType, sessionId, wpsHostKindForActivate))
            {
                _logger.LogInformation(
                    "[activate_tools] reject input={Input} reason=client_not_allowed plugin={Plugin} func={Func} clientType={ClientType}",
                    s,
                    plugin,
                    func,
                    clientType ?? "?");
                rejected.Add(s);
                continue;
            }

            if (state.ActivatedFunctionNames.Add(func))
            {
                activated.Add($"{plugin}.{func}");
                state.MarkExpansion();
            }
            else
                alreadyActive.Add($"{func}（插件 {plugin}）");
        }

        var msg = new StringBuilder();
        if (activated.Count > 0)
            msg.Append("[activate_tools] 已激活: ").Append(string.Join(", ", activated)).Append('\n');
        if (alreadyActive.Count > 0)
            msg.Append("[activate_tools] 以下已在本轮激活列表中，可直接用裸名 tool_calls 调用: ")
                .Append(string.Join(", ", alreadyActive)).Append('\n');
        if (rejected.Count > 0)
            msg.Append("[activate_tools] 未接受（不存在、格式无效或当前端不可用）: ").Append(string.Join(", ", rejected)).Append('\n');
        if (activated.Count == 0)
        {
            if (rejected.Count == 0 && alreadyActive.Count == 0)
            {
                if (skippedEmpty == toolNames.Length)
                    msg.Append("[activate_tools] toolNames 中均为空白字符串，请传入检索结果中的裸函数名或 Plugin.function。");
                else if (skippedMeta == toolNames.Length)
                    msg.Append("[activate_tools] 不能激活引导工具（search_available_tools / activate_tools），请传入业务工具名。");
                else if (skippedEmpty + skippedMeta == toolNames.Length)
                    msg.Append("[activate_tools] 仅含空白与/或引导工具名，无业务工具可激活；请用 search_available_tools 核对名称。");
                else
                    msg.Append("[activate_tools] 没有有效的工具名。");
            }
            else if (rejected.Count > 0)
                msg.Append("[activate_tools] 未能激活任何新工具；请用 search_available_tools 核对名称。");
        }

        var rejectedPreview = string.Join(", ", rejected.Take(16));
        if (rejected.Count > 16)
            rejectedPreview += ",…";
        _logger.LogInformation(
            "activate_tools activatedCount={Activated} rejectedCount={Rejected} rejectedPreview={RejectedPreview}",
            activated.Count,
            rejected.Count,
            string.IsNullOrEmpty(rejectedPreview) ? "-" : rejectedPreview);
        return Task.FromResult(msg.ToString().Trim());
    }
}
