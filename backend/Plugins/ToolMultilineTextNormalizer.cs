namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 将工具中的多行正文规范为「以 \n 分隔的逻辑行」，与 <see cref="WordParagraphSplitter"/> 规则一致：
/// <c>|</c>、空行、单行换行均会拆行，供 PPT <see cref="PptOpenXmlHelpers.SetShapeText"/> 等按行生成段落。
/// </summary>
public static class ToolMultilineTextNormalizer
{
    /// <summary>全空白视为空字符串；否则展开后仅用 <c>\n</c> 连接（无末尾多余换行除非原逻辑需保留）。</summary>
    public static string NormalizeToNewlineSeparatedLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs(text).ToList();
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }
}
