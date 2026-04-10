namespace OfficeCopilot.Server.Services;

/// <summary>
/// 仅依据 Kernel 函数名（不含插件前缀）推断只读/写倾向，供 <see cref="ToolCapabilityRegistry"/> 等共用，避免规则漂移。
/// </summary>
public static class KernelFunctionNameSemantics
{
    /// <summary>
    /// 函数名是否像只读查询类工具（优先于 <see cref="IsLikelyDestructive"/> 判断）。
    /// </summary>
    public static bool IsLikelyReadOnly(string fn)
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

    /// <summary>
    /// 函数名是否像会产生本机写副作用或结构性变更的工具。
    /// </summary>
    public static bool IsLikelyDestructive(string fn)
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

    /// <summary>
    /// 用户消息中出现的该函数名是否应视为「需要本机工具落地」的变异意图（非只读且像写操作）。
    /// </summary>
    public static bool ImpliesLocalMutation(string fn) =>
        !string.IsNullOrEmpty(fn) && !IsLikelyReadOnly(fn) && IsLikelyDestructive(fn);
}
