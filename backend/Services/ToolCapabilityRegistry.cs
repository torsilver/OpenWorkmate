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
            ["OfficeLegacy:office_legacy_save_as_open_xml"] = new(false, true, true, false),
            ["Browser:run_builtin_page_script"] = new(false, true, true, false),
            ["Browser:run_custom_javascript_in_page"] = new(false, true, true, false),
            ["CurrentDocument:current_run_document_script"] = new(false, true, true, false),
            ["CurrentDocument:current_run_custom_document_script"] = new(false, true, true, false),
            ["Subagent:run_subtask"] = new(false, false, false, false),
            ["Context:compact_conversation"] = new(false, true, false, false),
            ["ClawhubSkill:run_clawhub_script"] = new(false, true, true, false),
            ["Pdf:pdf_merge"] = new(false, true, false, false),
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

        if (KernelFunctionNameSemantics.IsLikelyReadOnly(fn))
            return new ToolCapability(ReadOnly: true, Destructive: false, SuggestHitl: false, AllowParallelSameTurn: true);

        if (KernelFunctionNameSemantics.IsLikelyDestructive(fn))
            return new ToolCapability(ReadOnly: false, Destructive: true, SuggestHitl: false, AllowParallelSameTurn: false);

        return Default;
    }
}
