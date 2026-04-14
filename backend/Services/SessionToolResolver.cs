using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services.DynamicTooling;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 按 clientType / 会话将「选中的函数对」解析为 <see cref="AITool"/> 列表；与主会话 <see cref="ChatService"/> 逻辑一致，供计划撰写等复用。
/// </summary>
public static class SessionToolResolver
{
    /// <summary>按 clientType 解析本轮暴露给模型的工具：使用 selectedPairs，或该端全量允许的函数。保底追加：Office/WPS 追加 current_run_document_script 与 current_run_custom_document_script；Chrome 追加 run_builtin_page_script 与 run_custom_javascript_in_page；所有端追加 run_command。</summary>
    public static IReadOnlyList<AITool>? ResolveToolsByClientType(
        ToolRegistry toolRegistry,
        IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs,
        string? clientType,
        string? sessionId)
    {
        IReadOnlyList<AITool>? result = null;
        if (selectedPairs is { Count: > 0 })
        {
            var filtered = ClientTypeToolFilter.Filter(selectedPairs, clientType, sessionId);
            if (filtered.Count > 0)
                result = GetToolsByPairs(toolRegistry, filtered);
        }
        result ??= toolRegistry.GetAllowedTools(clientType, sessionId);

        if (result != null)
        {
            void EnsureTool(string plugin, string func)
            {
                var tool = toolRegistry.FindTool(plugin, func);
                if (tool != null && !result!.Any(t => string.Equals(t.Name, func, StringComparison.OrdinalIgnoreCase)))
                    result = new List<AITool>(result) { tool };
            }

            if (IsOfficeOrWpsClient(clientType))
            {
                EnsureTool("CurrentDocument", "current_run_document_script");
                EnsureTool("CurrentDocument", "current_run_custom_document_script");
            }
            if (IsChromeClient(clientType))
            {
                EnsureTool("Browser", "run_builtin_page_script");
                EnsureTool("Browser", "run_custom_javascript_in_page");
            }
            EnsureTool("CLI", "run_command");
        }
        return result;
    }

    /// <summary>将 Plan 插件的 get_plan、update_plan、execute_plan_step、complete_plan 并入已选工具列表（供按计划执行与撰写时使用）。</summary>
    public static IReadOnlyList<AITool> MergePlanTools(ToolRegistry toolRegistry, IReadOnlyList<AITool> existing)
    {
        var set = new HashSet<string>(existing.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var list = new List<AITool>(existing);
        foreach (var funcName in new[] { "get_plan", "update_plan", "execute_plan_step", "complete_plan" })
        {
            var tool = toolRegistry.FindTool("Plan", funcName);
            if (tool != null && set.Add(funcName))
                list.Add(tool);
        }
        return list;
    }

    /// <summary>按 (插件名, 函数名) 列表从 ToolRegistry 中取出对应 AITool。</summary>
    public static IReadOnlyList<AITool> GetToolsByPairs(ToolRegistry toolRegistry, IReadOnlyList<(string PluginName, string FunctionName)> selected)
    {
        if (selected == null || selected.Count == 0) return Array.Empty<AITool>();
        var tools = new List<AITool>();
        foreach (var (pluginName, functionName) in selected)
        {
            var tool = toolRegistry.FindTool(pluginName, functionName);
            if (tool != null) tools.Add(tool);
        }
        return tools;
    }

    /// <summary>
    /// 历史配置项：曾为每个 Prompt 技能挂载独立工具。现已改为渐进式（<c>load_user_skill_instructions</c> + system 元数据），
    /// 此处仅在使用旧配置时打日志，不再向 bootstrap 注入 per-skill 函数。
    /// </summary>
    public static void AppendBootstrapUserSkills(
        List<AITool> list,
        ToolRegistry toolRegistry,
        string? clientType,
        string? sessionId,
        DynamicToolingConfig cfg,
        IReadOnlyList<SkillDefinition> skills,
        ILogger? logger)
    {
        _ = list;
        _ = toolRegistry;
        _ = clientType;
        _ = sessionId;
        if (!cfg.BootstrapIncludeAllEnabledUserSkills && cfg.BootstrapUserSkillIds is not { Count: > 0 })
            return;

        logger?.LogWarning(
            "Dynamic tooling bootstrap: bootstrapUserSkillIds / bootstrapIncludeAllEnabledUserSkills 已忽略（UserSkill 已改为渐进式：system 元数据 + {LoadFn}；当前 skills 条数={SkillCount}，见 DynamicToolingConfig 注释）。",
            DynamicToolingConstants.LoadUserSkillInstructionsFunctionName,
            skills.Count);
    }

    /// <summary>动态工具首轮：AgentTooling（search/activate）+ 各端保底脚本 + run_command；可选配置的 UserSkill；Chrome 含 run_builtin_page_script 与 run_custom_javascript_in_page；可选合并 Plan 四函数。</summary>
    public static IReadOnlyList<AITool> GetDynamicBootstrapTools(
        ToolRegistry toolRegistry,
        string? clientType,
        string? sessionId,
        bool mergePlanTools,
        DynamicToolingConfig? dynCfg = null,
        IReadOnlyList<SkillDefinition>? skillsForBootstrap = null,
        ILogger? bootstrapSkillLogger = null)
    {
        var list = new List<AITool>();
        void AddIf(string plugin, string func)
        {
            var t = toolRegistry.FindTool(plugin, func);
            if (t != null && !list.Any(x => string.Equals(x.Name, func, StringComparison.OrdinalIgnoreCase)))
                list.Add(t);
        }

        AddIf("AgentTooling", DynamicToolingConstants.SearchFunctionName);
        AddIf("AgentTooling", DynamicToolingConstants.ActivateFunctionName);
        if (IsOfficeOrWpsClient(clientType))
        {
            AddIf("CurrentDocument", "current_run_document_script");
            AddIf("CurrentDocument", "current_run_custom_document_script");
        }
        if (IsChromeClient(clientType))
        {
            AddIf("Browser", "run_builtin_page_script");
            AddIf("Browser", "run_custom_javascript_in_page");
        }
        AddIf("CLI", "run_command");
        AddIf("UserSkillProgressive", DynamicToolingConstants.SearchAvailableSkillsFunctionName);
        AddIf("UserSkillProgressive", DynamicToolingConstants.SelectSkillForTurnFunctionName);
        AddIf("UserSkillProgressive", DynamicToolingConstants.LoadUserSkillInstructionsFunctionName);

        if (dynCfg != null && skillsForBootstrap is { Count: > 0 })
            AppendBootstrapUserSkills(list, toolRegistry, clientType, sessionId, dynCfg, skillsForBootstrap, bootstrapSkillLogger);

        return mergePlanTools ? MergePlanTools(toolRegistry, list) : list;
    }

    /// <summary>按首轮记录的函数名顺序物化 bootstrap 工具列表。</summary>
    public static IReadOnlyList<AITool> MaterializeBootstrapFromOrderedFunctionNames(
        ToolRegistry registry,
        IReadOnlyList<string> orderedFunctionNames,
        string? clientType,
        string? sessionId,
        bool mergePlanTools)
    {
        var list = new List<AITool>();
        foreach (var func in orderedFunctionNames)
        {
            if (string.IsNullOrEmpty(func)) continue;
            if (!registry.TryGetPluginName(func, out var plugin)) continue;
            if (!ClientTypeToolFilter.IsAllowed(plugin, func, clientType, sessionId)) continue;
            var tool = registry.FindTool(plugin, func);
            if (tool != null && !list.Any(x => string.Equals(x.Name, func, StringComparison.OrdinalIgnoreCase)))
                list.Add(tool);
        }
        return mergePlanTools ? MergePlanTools(registry, list) : list;
    }

    /// <summary>动态工具当前外层 pass 应绑定到模型的完整列表：bootstrap ∪ 已激活业务工具。</summary>
    public static IReadOnlyList<AITool> BuildDynamicActiveToolList(
        ToolRegistry registry,
        DynamicToolingTurnState state,
        string? clientType,
        string? sessionId,
        bool mergePlanTools)
    {
        IReadOnlyList<AITool> bootstrap = state.BootstrapFunctionNamesOrder.Count > 0
            ? MaterializeBootstrapFromOrderedFunctionNames(
                registry, state.BootstrapFunctionNamesOrder, clientType, sessionId, mergePlanTools)
            : GetDynamicBootstrapTools(registry, clientType, sessionId, mergePlanTools);

        var set = new HashSet<string>(bootstrap.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var list = new List<AITool>(bootstrap);
        foreach (var func in state.ActivatedFunctionNames)
        {
            if (DynamicToolingConstants.MetaFunctionNames.Contains(func))
                continue;
            if (!registry.TryGetPluginName(func, out var plugin))
                continue;
            if (!ClientTypeToolFilter.IsAllowed(plugin, func, clientType, sessionId))
                continue;
            var tool = registry.FindTool(plugin, func);
            if (tool != null && set.Add(func))
                list.Add(tool);
        }
        return list;
    }

    private static bool IsChromeClient(string? clientType)
    {
        var ct = (clientType ?? "").Trim();
        return string.IsNullOrEmpty(ct) || string.Equals(ct, "chrome", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOfficeOrWpsClient(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)) return false;
        var ct = clientType.Trim();
        return string.Equals(ct, "office-word", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "office-excel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "office-powerpoint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase);
    }
}
