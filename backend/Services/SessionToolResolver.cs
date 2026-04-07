using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 按 clientType / 会话将「选中的函数对」解析为 <see cref="AITool"/> 列表；与主会话 <see cref="ChatService"/> 逻辑一致，供计划撰写等复用。
/// </summary>
public static class SessionToolResolver
{
    /// <summary>按 clientType 解析本轮暴露给模型的工具：使用 selectedPairs，或该端全量允许的函数。保底追加：Office/WPS 追加 current_run_document_script；Chrome 追加 run_page_script；所有端追加 run_command。</summary>
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
                EnsureTool("Browser", "run_custom_page_script");
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
