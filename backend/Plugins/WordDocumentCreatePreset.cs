namespace OfficeCopilot.Server.Plugins;

/// <summary><see cref="WordPlugin.WordDocumentCreate"/> 的版式预设：default 保持历史西式默认；cnGovGbt9704 为中文正式稿默认（GB/T 9704 常用归纳，依赖本机字体）。</summary>
public enum WordDocumentCreatePreset
{
    Default,
    CnGovGbt9704
}

public static class WordDocumentCreatePresetParser
{
    /// <summary>解析工具参数 <c>documentPreset</c>；空或 default 为通用预设。</summary>
    public static bool TryParse(string? raw, out WordDocumentCreatePreset preset, out string? errorMessage)
    {
        preset = WordDocumentCreatePreset.Default;
        errorMessage = null;
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Equals("default", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Equals("cnGovGbt9704", StringComparison.OrdinalIgnoreCase))
        {
            preset = WordDocumentCreatePreset.CnGovGbt9704;
            return true;
        }

        errorMessage =
            "[错误] documentPreset 无效：请使用 default（通用 Office 默认字体与页边距）或 cnGovGbt9704（中文正式稿默认、GB/T 9704—2012 常用版式归纳：天头/订口近似值、正文 3 号仿宋、固定 28 磅行距、标题 2 号小标宋居中；小标宋/仿宋等依赖本机已安装字体）。";
        return false;
    }
}
