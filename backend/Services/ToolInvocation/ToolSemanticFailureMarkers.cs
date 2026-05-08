namespace OpenWorkmate.Server.Services.ToolInvocation;

/// <summary>
/// 工具返回串的「语义失败」固定前缀协议：供 <see cref="ToolStatusNotifier"/> 与 <see cref="ToolInvocationFailureClassifier"/> 共用，
/// 禁止用正文子串 Contains("错误") 等启发式（检索类工具会拼接技能/工具说明，易误判）。
/// </summary>
/// <remarks>
/// 新增失败样式时：在此表追加前缀，并同步 <c>.cursor/rules/tool-result-protocol.mdc</c> 与单元测试。
/// </remarks>
public static class ToolSemanticFailureMarkers
{
    /// <summary>按长度降序排列，保证 <c>[MCP 工具错误]</c> 等较长前缀先于 <c>[MCP </c> 匹配（虽 StartsWith 短前缀也能覆盖长串，显式顺序便于阅读）。</summary>
    private static readonly string[] SemanticFailurePrefixes =
    [
        "[参数绑定失败]",
        "[MCP 工具错误]",
        "[MCP 调用异常]",
        "[MCP ",
        // Microsoft.Extensions.AI FunctionInvokingChatClient 在工具异常且未向外抛时写入结果串（CreateFunctionResultContent）。
        "Error: Requested function",
        "Error: Function failed.",
        "Error: Unknown error.",
        "[工具调用失败]",
        "[错误]",
        "[系统拦截]",
        "[用户拒绝]",
        "[保存失败]",
        "[无效]",
        "[记忆未启用]",
        "[创建失败]",
        "[更新失败]",
        "[技能生成失败]",
    ];

    /// <summary>
    /// 在 <c>success==true</c> 时用于判断返回体是否仍应视为「对用户/统计的失败」；
    /// 仅当 Trim 后以已知失败前缀开头时为 true。
    /// </summary>
    public static bool LooksLikeSemanticFailure(string? content)
    {
        var s = NormalizeForPrefixCheck(content);
        if (s.Length == 0) return false;
        foreach (var p in SemanticFailurePrefixes)
        {
            if (s.StartsWith(p, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 在已判定为失败时细分 Binding / Mcp / Business，与 <see cref="AgentDebugStatsService.RecordToolInvocation"/> 聚合一致。
    /// </summary>
    public static ToolInvocationFailureKind ClassifyFailureKind(string? fullResult)
    {
        var s = NormalizeForPrefixCheck(fullResult);
        if (s.Length == 0)
            return ToolInvocationFailureKind.Business;
        if (s.StartsWith("[参数绑定失败]", StringComparison.Ordinal))
            return ToolInvocationFailureKind.Binding;
        if (s.StartsWith("[MCP 工具错误]", StringComparison.Ordinal)
            || s.StartsWith("[MCP 调用异常]", StringComparison.Ordinal)
            || s.StartsWith("[MCP ", StringComparison.Ordinal))
            return ToolInvocationFailureKind.Mcp;
        return ToolInvocationFailureKind.Business;
    }

    private static string NormalizeForPrefixCheck(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";
        var s = content.TrimStart();
        if (s.Length > 0 && s[0] == '\uFEFF')
            s = s.TrimStart('\uFEFF').TrimStart();
        return s;
    }
}
