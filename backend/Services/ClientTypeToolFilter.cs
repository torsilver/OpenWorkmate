namespace OfficeCopilot.Server.Services;

/// <summary>按 clientType 过滤暴露给模型的工具集：Chrome 不暴露 CurrentDocument；Office/WPS 只暴露 CurrentDocument + 通用插件，不暴露 Browser/File/CLI/Word/Excel。</summary>
public static class ClientTypeToolFilter
{
    private static readonly StringComparer PluginComparer = StringComparer.OrdinalIgnoreCase;

    private static bool IsCommonPlugin(string pluginName)
    {
        if (string.IsNullOrEmpty(pluginName)) return false;
        return PluginComparer.Equals(pluginName, "Memory")
            || PluginComparer.Equals(pluginName, "Context")
            || PluginComparer.Equals(pluginName, "Subagent")
            || PluginComparer.Equals(pluginName, "CrossAgentTask")
            || PluginComparer.Equals(pluginName, "ClawhubSkill")
            || PluginComparer.Equals(pluginName, "AccurateData")
            || PluginComparer.Equals(pluginName, "MeetingTranscript")
            || PluginComparer.Equals(pluginName, "ScheduledTask")
            || PluginComparer.Equals(pluginName, "SkillAuthor")
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
        return PluginComparer.Equals(functionName, "current_run_document_script")
            || PluginComparer.Equals(functionName, "current_run_custom_document_script");
    }

    private static bool IsCurrentDocumentPptFunction(string functionName)
    {
        return PluginComparer.Equals(functionName, "current_ppt_slides_list")
            || PluginComparer.Equals(functionName, "current_ppt_slide_read")
            || PluginComparer.Equals(functionName, "current_ppt_slide_write")
            || PluginComparer.Equals(functionName, "current_ppt_slide_insert")
            || PluginComparer.Equals(functionName, "current_ppt_slide_delete")
            || PluginComparer.Equals(functionName, "current_ppt_slide_image_add")
            || PluginComparer.Equals(functionName, "current_ppt_notes_read")
            || PluginComparer.Equals(functionName, "current_ppt_notes_write")
            || PluginComparer.Equals(functionName, "current_ppt_slides_reorder")
            || PluginComparer.Equals(functionName, "current_ppt_table_create")
            || PluginComparer.Equals(functionName, "current_ppt_table_write_cells")
            || PluginComparer.Equals(functionName, "current_ppt_hyperlink_add")
            || PluginComparer.Equals(functionName, "current_ppt_slide_duplicate");
    }

    /// <summary>定时任务 Runner 使用的会话：禁止在执行轮次内再创建/改/删任务，避免套娃。</summary>
    public static bool IsScheduledTaskRunnerSession(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && sessionId.StartsWith("scheduled:", StringComparison.OrdinalIgnoreCase);

    private static bool IsScheduledTaskMutationFunction(string functionName) =>
        PluginComparer.Equals(functionName, "scheduled_task_create")
        || PluginComparer.Equals(functionName, "scheduled_task_update")
        || PluginComparer.Equals(functionName, "scheduled_task_delete");

    /// <summary>判断该 (Plugin, Function) 是否允许暴露给给定 clientType 的会话。</summary>
    /// <param name="sessionId">非空且以 <c>scheduled:</c> 开头时，屏蔽 ScheduledTask 的创建/更新/删除工具。</param>
    public static bool IsAllowed(string pluginName, string functionName, string? clientType, string? sessionId = null)
    {
        if (string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(functionName))
            return false;

        if (IsScheduledTaskRunnerSession(sessionId)
            && PluginComparer.Equals(pluginName, "ScheduledTask")
            && IsScheduledTaskMutationFunction(functionName))
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

    /// <summary>过滤 (PluginName, FunctionName) 列表，只保留该 clientType 允许的项。</summary>
    public static IReadOnlyList<(string PluginName, string FunctionName)> Filter(
        IReadOnlyList<(string PluginName, string FunctionName)> pairs,
        string? clientType,
        string? sessionId = null)
    {
        if (pairs == null || pairs.Count == 0) return Array.Empty<(string, string)>();
        var list = new List<(string, string)>();
        foreach (var (p, f) in pairs)
        {
            if (IsAllowed(p, f, clientType, sessionId))
                list.Add((p, f));
        }
        return list;
    }
}
