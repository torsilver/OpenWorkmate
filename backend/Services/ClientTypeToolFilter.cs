namespace OfficeCopilot.Server.Services;

/// <summary>按 clientType 过滤暴露给模型的工具集：Chrome 不暴露 CurrentDocument；Office/WPS 暴露 CurrentDocument + 通用插件 + Pdf（路径须为后端进程可读）。通用插件含 <c>AgentTooling</c>（动态工具引导）、与本机能力对齐的 <c>CLI</c>、<c>File</c>、<c>System</c>、<c>UserOptions</c> 等。仍不暴露 <c>Browser</c>（仅 Chrome 任务上下文）及磁盘型 Word/Excel/Ppt/OfficeLegacy 插件（与 <c>CurrentDocument</c> RPC 区分）。</summary>
public static class ClientTypeToolFilter
{
    private static readonly StringComparer PluginComparer = StringComparer.OrdinalIgnoreCase;

    private static bool IsPdfPlugin(string pluginName) =>
        PluginComparer.Equals(pluginName, "Pdf");

    private static bool IsCommonPlugin(string pluginName)
    {
        if (string.IsNullOrEmpty(pluginName)) return false;
        return PluginComparer.Equals(pluginName, "Memory")
            || PluginComparer.Equals(pluginName, "Context")
            || PluginComparer.Equals(pluginName, "CLI")
            || PluginComparer.Equals(pluginName, "File")
            || PluginComparer.Equals(pluginName, "System")
            || PluginComparer.Equals(pluginName, "UserOptions")
            || PluginComparer.Equals(pluginName, "AgentTooling")
            || PluginComparer.Equals(pluginName, "Subagent")
            || PluginComparer.Equals(pluginName, "CrossAgentTask")
            || PluginComparer.Equals(pluginName, "ClawhubSkill")
            || PluginComparer.Equals(pluginName, "AccurateData")
            || PluginComparer.Equals(pluginName, "MeetingTranscript")
            || PluginComparer.Equals(pluginName, "ScheduledTask")
            || PluginComparer.Equals(pluginName, "SkillAuthor")
            || PluginComparer.Equals(pluginName, "UserSkillProgressive")
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
        return PluginComparer.Equals(functionName, "current_ppt_document_create")
            || PluginComparer.Equals(functionName, "current_ppt_slides_list")
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

    /// <summary>WPS 下仅当宿主为 word/et/wpp 时收窄 CurrentDocument；unknown/none/未上报不收紧。</summary>
    private static string? WpsNarrowCurrentDocumentHost(string? wpsHostKind)
    {
        var n = ClientPageContextSuffixBuilder.NormalizeWpsHostKind(wpsHostKind);
        if (n == "unknown" || n == "none")
            return null;
        if (PluginComparer.Equals(n, "word") || PluginComparer.Equals(n, "et") || PluginComparer.Equals(n, "wpp"))
            return n;
        return null;
    }

    /// <summary>判断该 (Plugin, Function) 是否允许暴露给给定 clientType 的会话。</summary>
    /// <param name="sessionId">非空且以 <c>scheduled:</c> 开头时，屏蔽 ScheduledTask 的创建/更新/删除工具。</param>
    /// <param name="wpsHostKind">仅 <c>wps</c> 有效：set_context 上报的宿主；为 <c>word</c>/<c>et</c>/<c>wpp</c> 时 CurrentDocument 与对应 office-* 子集一致。</param>
    public static bool IsAllowed(string pluginName, string functionName, string? clientType, string? sessionId = null, string? wpsHostKind = null)
    {
        if (string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(functionName))
            return false;

        if (IsScheduledTaskRunnerSession(sessionId)
            && PluginComparer.Equals(pluginName, "ScheduledTask")
            && IsScheduledTaskMutationFunction(functionName))
            return false;

        if (PluginComparer.Equals(pluginName, "Subagent") && !IsSubagentFunctionAllowed(functionName, clientType, sessionId, wpsHostKind))
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
            if (IsPdfPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
                return IsCurrentDocumentWordFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
            return false;
        }

        if (PluginComparer.Equals(ct, "office-excel"))
        {
            if (IsCommonPlugin(pluginName)) return true;
            if (IsPdfPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
                return IsCurrentDocumentExcelFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
            return false;
        }

        if (PluginComparer.Equals(ct, "office-powerpoint"))
        {
            if (IsCommonPlugin(pluginName)) return true;
            if (IsPdfPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
                return IsCurrentDocumentPptFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
            return false;
        }

        if (PluginComparer.Equals(ct, "wps"))
        {
            if (IsCommonPlugin(pluginName)) return true;
            if (IsPdfPlugin(pluginName)) return true;
            if (PluginComparer.Equals(pluginName, "CurrentDocument"))
            {
                var narrow = WpsNarrowCurrentDocumentHost(wpsHostKind);
                if (narrow == null)
                    return IsCurrentDocumentWordFunction(functionName) || IsCurrentDocumentExcelFunction(functionName) || IsCurrentDocumentPptFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
                if (PluginComparer.Equals(narrow, "word"))
                    return IsCurrentDocumentWordFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
                if (PluginComparer.Equals(narrow, "et"))
                    return IsCurrentDocumentExcelFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
                if (PluginComparer.Equals(narrow, "wpp"))
                    return IsCurrentDocumentPptFunction(functionName) || IsCurrentDocumentScriptFunction(functionName);
                return false;
            }
            return false;
        }

        return true;
    }

    /// <summary>过滤 (PluginName, FunctionName) 列表，只保留该 clientType 允许的项。</summary>
    public static IReadOnlyList<(string PluginName, string FunctionName)> Filter(
        IReadOnlyList<(string PluginName, string FunctionName)> pairs,
        string? clientType,
        string? sessionId = null,
        string? wpsHostKind = null)
    {
        if (pairs == null || pairs.Count == 0) return Array.Empty<(string, string)>();
        var list = new List<(string, string)>();
        foreach (var (p, f) in pairs)
        {
            if (IsAllowed(p, f, clientType, sessionId, wpsHostKind))
                list.Add((p, f));
        }
        return list;
    }

    /// <summary>内置子任务入口按端裁剪：<c>run_browser_subtask</c> 仅 Chrome；<c>run_cli_subtask</c> 仅当该端可暴露 <c>CLI:run_command</c> 时。</summary>
    private static bool IsSubagentFunctionAllowed(string functionName, string? clientType, string? sessionId, string? wpsHostKind)
    {
        // 临时：不向模型暴露通用子任务 run_subtask（插件仍注册，便于日后恢复；子代理管线与其它入口不变）。
        if (PluginComparer.Equals(functionName, "run_subtask"))
            return false;

        if (PluginComparer.Equals(functionName, "run_browser_subtask"))
        {
            var ct0 = (clientType ?? "").Trim();
            if (string.IsNullOrEmpty(ct0))
                ct0 = "chrome";
            return PluginComparer.Equals(ct0, "chrome");
        }

        if (PluginComparer.Equals(functionName, "run_cli_subtask"))
            return IsAllowed("CLI", "run_command", clientType, sessionId, wpsHostKind);

        return true;
    }
}
