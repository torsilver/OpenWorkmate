using System.Text.Json;

namespace OfficeCopilot.Server.Services;

/// <summary>解析工具需求门控模型输出：仅当明确为「不需要工具」时返回 false；否则 true（保守，避免误判导致无法操作本机）。</summary>
public static class ToolNeedGateParser
{
    /// <summary>
    /// <paramref name="bindTools"/>：是否应向主会话绑定工具（true = 需要工具路径）。
    /// <paramref name="parsedExplicitly"/>：是否从 YES/NO 或 JSON 中明确解析到（用于统计/调试）。
    /// </summary>
    public static (bool BindTools, bool ParsedExplicitly) Parse(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            return (true, false);

        if (s.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                if (root.TryGetProperty("needTools", out var nt) && nt.ValueKind == JsonValueKind.True)
                    return (true, true);
                if (root.TryGetProperty("needTools", out var nt2) && nt2.ValueKind == JsonValueKind.False)
                    return (false, true);
                if (root.TryGetProperty("need_tools", out var nt3) && nt3.ValueKind == JsonValueKind.True)
                    return (true, true);
                if (root.TryGetProperty("need_tools", out var nt4) && nt4.ValueKind == JsonValueKind.False)
                    return (false, true);
            }
            catch
            {
                /* fall through */
            }
        }

        var firstLine = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? s;
        firstLine = firstLine.Trim().TrimStart('*', '_', '`').TrimEnd('*', '_', '`', '。', '.');
        var token = firstLine.Split((char[])[' ', '\t', '：', ':'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? firstLine;
        token = token.Trim('\"', '\'', '*', '`');

        if (string.Equals(token, "NO", StringComparison.OrdinalIgnoreCase))
            return (false, true);
        if (string.Equals(token, "YES", StringComparison.OrdinalIgnoreCase))
            return (true, true);

        return (true, false);
    }
}
