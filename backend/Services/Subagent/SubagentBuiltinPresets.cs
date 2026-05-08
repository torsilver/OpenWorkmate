using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services;

namespace OpenWorkmate.Server.Services.Subagent;

/// <summary>内置子代理：system 文案与工具白名单（与 <see cref="ToolRegistry.GetAllowedTools"/> 结果求交）。</summary>
public static class SubagentBuiltinPresets
{
    private static readonly StringComparer NameCmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>主会话可调用的子任务入口工具名；子代理内须全部排除。</summary>
    public static readonly HashSet<string> SubagentEntryToolNames = new(NameCmp)
    {
        "run_subtask",
        "run_explore_subtask",
        "run_cli_subtask",
        "run_browser_subtask",
    };

    public static bool IsSubagentEntryToolName(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && SubagentEntryToolNames.Contains(toolName);

    public static string BuildSystemInstructions(SubagentBuiltinPreset preset)
    {
        const string baseCore =
            "你是一个子代理。请完成用户给出的子任务，可使用现有工具。完成后仅用一段自然语言总结最终结果，不要逐步解释过程。"
            + " 用户最新表述优先于历史中的旧结论；本机/文档/网页的当前状态须用工具查询后再下结论，勿仅凭聊天记录推断。"
            + " 若子任务涉及修改本机文件或 Office 文档，须实际调用工具并依据工具返回再总结；不得未调用工具却声称已完成变更。";

        return preset switch
        {
            SubagentBuiltinPreset.Explore => baseCore
                + " 你是「探索」子代理：专注只读式检索与阅读（文件、文档、上下文、必要时浏览器只读信息），可多次调用工具。"
                + " 将路径、关键片段与结论压缩为要点；不要把冗长原始输出复述进总结。",
            SubagentBuiltinPreset.CliShell => baseCore
                + " 你是「终端」子代理：以命令行工具为主完成目标；命令与标准输出可能很长，请在子任务内消化，总结中只保留关键命令、退出码与结论。",
            SubagentBuiltinPreset.Browser => baseCore
                + " 你是「浏览器」子代理：通过页内脚本完成网页侧操作与取证；DOM 与脚本结果可能很冗长，总结中只保留与用户目标直接相关的结论与证据。",
            _ => baseCore,
        };
    }

    /// <summary>
    /// 从「当前端已允许」的工具列表中筛出子代理可用工具：<paramref name="preset"/> 非 <see cref="SubagentBuiltinPreset.General"/> 时按插件白名单求交。
    /// </summary>
    public static List<AITool> FilterToolsForSubtask(
        ToolRegistry registry,
        IReadOnlyList<AITool> sessionAllowed,
        SubagentBuiltinPreset preset,
        [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        var scratch = new List<AITool>();
        foreach (var t in sessionAllowed)
        {
            var n = t.Name;
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (NameCmp.Equals(n, "compact_conversation")) continue;
            if (IsSubagentEntryToolName(n)) continue;
            scratch.Add(t);
        }

        if (preset == SubagentBuiltinPreset.General)
        {
            if (scratch.Count == 0)
            {
                errorMessage = "[错误] 当前端无可用的工具集，无法执行子任务。";
                return new List<AITool>();
            }

            return scratch;
        }

        static bool PluginAllowed(SubagentBuiltinPreset p, string plugin)
        {
            return p switch
            {
                SubagentBuiltinPreset.Explore => NameCmp.Equals(plugin, "File")
                    || NameCmp.Equals(plugin, "Context")
                    || NameCmp.Equals(plugin, "Memory")
                    || NameCmp.Equals(plugin, "Browser")
                    || NameCmp.Equals(plugin, "CurrentDocument"),
                SubagentBuiltinPreset.CliShell => NameCmp.Equals(plugin, "CLI"),
                SubagentBuiltinPreset.Browser => NameCmp.Equals(plugin, "Browser"),
                _ => true,
            };
        }

        var filtered = new List<AITool>();
        foreach (var t in scratch)
        {
            var n = t.Name!;
            if (!registry.TryGetPluginName(n, out var plugin)) continue;
            if (PluginAllowed(preset, plugin))
                filtered.Add(t);
        }

        if (filtered.Count == 0)
        {
            errorMessage = preset switch
            {
                SubagentBuiltinPreset.Explore =>
                    "[错误] 当前端无可用探索类工具（需 File/Context/Memory 等；Chrome 还可含 Browser；Office/WPS 可含 CurrentDocument 只读类函数）。",
                SubagentBuiltinPreset.CliShell =>
                    "[错误] 当前端未开放终端工具，无法使用 CLI 子代理。",
                SubagentBuiltinPreset.Browser =>
                    "[错误] 当前端未开放浏览器工具，无法使用浏览器子代理。",
                _ => "[错误] 当前端无可用的工具集，无法执行子任务。",
            };
            return new List<AITool>();
        }

        return filtered;
    }
}
