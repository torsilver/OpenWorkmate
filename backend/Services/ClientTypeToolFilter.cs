using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services;

/// <summary>按 clientType 过滤暴露给模型的工具集：Chrome 不暴露 CurrentDocument；Office/WPS 只暴露 CurrentDocument + 通用插件，不暴露 Browser/File/CLI/Word/Excel。</summary>
public static class ClientTypeToolFilter
{
    private static readonly StringComparer PluginComparer = StringComparer.OrdinalIgnoreCase;

    private static bool IsCommonPlugin(string pluginName)
    {
        if (string.IsNullOrEmpty(pluginName)) return false;
        return PluginComparer.Equals(pluginName, "Tavily")
            || PluginComparer.Equals(pluginName, "Memory")
            || PluginComparer.Equals(pluginName, "Context")
            || PluginComparer.Equals(pluginName, "Subagent")
            || PluginComparer.Equals(pluginName, "CrossAgentTask")
            || PluginComparer.Equals(pluginName, "ClawhubSkill")
            || PluginComparer.Equals(pluginName, "AccurateData")
            || PluginComparer.Equals(pluginName, "ScheduledTask")
            || pluginName.StartsWith("UserSkill_", StringComparison.OrdinalIgnoreCase)
            || pluginName.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentDocumentWordFunction(string functionName)
    {
        return PluginComparer.Equals(functionName, "current_word_insert_text")
            || PluginComparer.Equals(functionName, "current_word_read_body")
            || PluginComparer.Equals(functionName, "current_word_read_selection")
            || PluginComparer.Equals(functionName, "current_word_insert_table")
            || PluginComparer.Equals(functionName, "current_word_search_replace");
    }

    private static bool IsCurrentDocumentExcelFunction(string functionName)
    {
        return PluginComparer.Equals(functionName, "current_excel_read_range")
            || PluginComparer.Equals(functionName, "current_excel_write_range")
            || PluginComparer.Equals(functionName, "current_excel_list_sheets")
            || PluginComparer.Equals(functionName, "current_excel_get_used_range")
            || PluginComparer.Equals(functionName, "current_excel_read_formulas")
            || PluginComparer.Equals(functionName, "current_excel_write_formulas");
    }

    private static bool IsCurrentDocumentScriptFunction(string functionName)
    {
        return PluginComparer.Equals(functionName, "current_run_document_script");
    }

    private static bool IsCurrentDocumentPptFunction(string functionName)
    {
        return PluginComparer.Equals(functionName, "current_ppt_slides_list")
            || PluginComparer.Equals(functionName, "current_ppt_slide_read");
    }

    /// <summary>判断该 (Plugin, Function) 是否允许暴露给给定 clientType 的会话。</summary>
    public static bool IsAllowed(string pluginName, string functionName, string? clientType)
    {
        if (string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(functionName))
            return false;

        var ct = (clientType ?? "").Trim();
        if (string.IsNullOrEmpty(ct))
            ct = "chrome";

        if (PluginComparer.Equals(ct, "chrome"))
        {
            return !PluginComparer.Equals(pluginName, "CurrentDocument");
        }

        if (PluginComparer.Equals(ct, "office-word"))
        {
            if (IsCommonPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
                return IsCurrentDocumentWordFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
            return false;
        }

        if (PluginComparer.Equals(ct, "office-excel"))
        {
            if (IsCommonPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
                return IsCurrentDocumentExcelFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
            return false;
        }

        if (PluginComparer.Equals(ct, "office-powerpoint"))
        {
            if (IsCommonPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
                return IsCurrentDocumentPptFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
            return false;
        }

        if (PluginComparer.Equals(ct, "wps"))
        {
            if (IsCommonPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
                return IsCurrentDocumentWordFunction(functionName) || IsCurrentDocumentExcelFunction(functionName) || IsCurrentDocumentPptFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
            return false;
        }

        return true;
    }

    /// <summary>从 Kernel 中取出该 clientType 允许的全部函数，用于「全量工具」路径的按端过滤。</summary>
    public static IReadOnlyList<KernelFunction> GetAllowedFunctions(Kernel kernel, string? clientType)
    {
        if (kernel == null) return Array.Empty<KernelFunction>();
        var list = new List<KernelFunction>();
        foreach (var plugin in kernel.Plugins)
        {
            foreach (KernelFunction func in plugin)
            {
                if (IsAllowed(plugin.Name, func.Name, clientType))
                    list.Add(func);
            }
        }
        return list;
    }

    /// <summary>过滤 (PluginName, FunctionName) 列表，只保留该 clientType 允许的项。</summary>
    public static IReadOnlyList<(string PluginName, string FunctionName)> Filter(
        IReadOnlyList<(string PluginName, string FunctionName)> pairs,
        string? clientType)
    {
        if (pairs == null || pairs.Count == 0) return Array.Empty<(string, string)>();
        var list = new List<(string, string)>();
        foreach (var (p, f) in pairs)
        {
            if (IsAllowed(p, f, clientType))
                list.Add((p, f));
        }
        return list;
    }
}
