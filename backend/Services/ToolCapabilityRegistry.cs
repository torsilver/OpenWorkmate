using System.Collections.Frozen;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 工具能力元数据（对标 Claude Code 类 harness 的 read-only / destructive / HITL 提示），供日志与未来并行策略使用。
/// </summary>
public readonly record struct ToolCapability(
    bool ReadOnly,
    bool Destructive,
    bool SuggestHitl,
    bool AllowParallelSameTurn);

/// <summary>
/// 按插件名 + 函数名解析 <see cref="ToolCapability"/>：显式表优先，其余用语义启发式。
/// </summary>
public static class ToolCapabilityRegistry
{
    private static readonly ToolCapability Default = new(ReadOnly: false, Destructive: false, SuggestHitl: false, AllowParallelSameTurn: false);

    private static readonly FrozenDictionary<string, ToolCapability> Exact = BuildExact();

    private static FrozenDictionary<string, ToolCapability> BuildExact()
    {
        var d = new Dictionary<string, ToolCapability>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLI:run_command"] = new(false, true, true, false),
            ["Browser:run_page_script"] = new(false, true, true, false),
            ["Browser:run_custom_page_script"] = new(false, true, true, false),
            ["CurrentDocument:current_run_document_script"] = new(false, true, true, false),
            ["CurrentDocument:current_run_custom_document_script"] = new(false, true, true, false),
            ["Subagent:run_subtask"] = new(false, false, false, false),
            ["Context:compact_conversation"] = new(false, true, false, false),
            ["ClawhubSkill:run_clawhub_script"] = new(false, true, true, false),
        };
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>规范键：<c>Plugin:function</c>，大小写不敏感。</summary>
    public static string NormalizeKey(string pluginName, string functionName) =>
        $"{pluginName?.Trim() ?? ""}:{functionName?.Trim() ?? ""}";

    public static ToolCapability Get(string? pluginName, string? functionName)
    {
        var plugin = pluginName ?? "";
        var fn = functionName ?? "";
        var key = NormalizeKey(plugin, fn);
        if (Exact.TryGetValue(key, out var cap))
            return cap;

        if (IsLikelyReadOnly(fn))
            return new ToolCapability(ReadOnly: true, Destructive: false, SuggestHitl: false, AllowParallelSameTurn: true);

        if (IsLikelyDestructive(fn))
            return new ToolCapability(ReadOnly: false, Destructive: true, SuggestHitl: false, AllowParallelSameTurn: false);

        return Default;
    }

    private static bool IsLikelyReadOnly(string fn)
    {
        if (string.IsNullOrEmpty(fn)) return false;
        if (fn.StartsWith("get_", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.EndsWith("_read", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.EndsWith("_list", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.EndsWith("_meta", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(fn, "search_memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fn, "search_accurate_data", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool IsLikelyDestructive(string fn)
    {
        if (string.IsNullOrEmpty(fn)) return false;
        if (fn.EndsWith("_write", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.EndsWith("_delete", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.EndsWith("_remove", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.Contains("_insert", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.EndsWith("_add", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.Contains("_create", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.Contains("_set", StringComparison.OrdinalIgnoreCase) && !fn.Contains("_list", StringComparison.OrdinalIgnoreCase)) return true;
        if (fn.Contains("_clear", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(fn, "save_memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fn, "accurate_data_write", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fn, "execute_plan_step", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fn, "create_plan", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
