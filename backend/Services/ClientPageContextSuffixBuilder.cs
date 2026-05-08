using System.Text;

namespace OpenWorkmate.Server.Services;

/// <summary>将 set_context 存储的 WPS 宿主与页签/展示名拼成追加到 system（IdentitySuffix）的短说明。</summary>
public static class ClientPageContextSuffixBuilder
{
    public const int PageTitleInjectMaxChars = 200;

    /// <summary>将客户端上报的 wpsHostKind 规范为小写白名单值；非法则返回 null。</summary>
    public static string? NormalizeWpsHostKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        return v switch
        {
            "word" or "et" or "wpp" or "unknown" or "none" => v,
            _ => null
        };
    }

    /// <summary>供本轮流式 system 后缀使用；与持久化历史无关。</summary>
    public static string Build(string? clientType, string? storedWpsHostKind, string? pageContextTitle)
    {
        var ct = (clientType ?? "").Trim();
        var sb = new StringBuilder();

        if (string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase))
        {
            var hostLine = BuildWpsHostInstruction(storedWpsHostKind);
            if (!string.IsNullOrEmpty(hostLine))
                sb.Append(hostLine);
        }

        var title = (pageContextTitle ?? "").Trim();
        if (title.Length > 0)
        {
            if (title.Length > PageTitleInjectMaxChars)
                title = title[..PageTitleInjectMaxChars] + "…";
            if (sb.Length > 0) sb.Append("\n\n");
            if (string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("活动文档展示名（仅供参考，不得以文件名推断宿主或文件类型）：");
                sb.Append(title);
            }
            else
            {
                sb.Append("当前浏览器活动标签标题（仅供参考）：");
                sb.Append(title);
            }
        }

        return sb.Length == 0 ? "" : sb.ToString();
    }

    /// <summary>仅 WPS 宿主说明；unknown/none 也给短提示，避免模型盲猜 Word。</summary>
    public static string? BuildWpsHostInstruction(string? storedWpsHostKind)
    {
        var hk = (storedWpsHostKind ?? "").Trim().ToLowerInvariant();
        return hk switch
        {
            "word" =>
                "【WPS 当前宿主】文字处理。请优先使用 current_word_* 等 CurrentDocument 工具；勿把「当前文档」默认当成表格。",
            "et" =>
                "【WPS 当前宿主】电子表格。请优先使用 current_excel_* 等 CurrentDocument 工具；勿把「当前文档」默认当成文字。",
            "wpp" =>
                "【WPS 当前宿主】演示文稿。请优先使用 current_ppt_* 等 CurrentDocument 工具。",
            "unknown" or "none" or "" =>
                "【WPS 当前宿主】客户端未明确上报宿主类型（unknown/none）。选择 CurrentDocument 工具前请先确认当前窗口是文字、表格还是演示，必要时先调用与宿主匹配的只读工具试探。",
            _ => null
        };
    }
}
