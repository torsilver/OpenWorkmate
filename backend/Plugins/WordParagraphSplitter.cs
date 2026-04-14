using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 将 <see cref="WordPlugin.WordDocumentCreate"/> 的 paragraphs（由 <c>string[]</c> 归并得到的单串）展开为多条逻辑行：
/// 先按 <c>|</c> 分段，再在每段内按空行与换行拆分，减轻模型少用 <c>|</c> 时出现单段「挤成一坨」的问题。
/// </summary>
public static class WordParagraphSplitter
{
    private static readonly Regex BlankLineSplitter = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);

    /// <summary>对完整 paragraphs 字符串：先 <c>|</c> 切分，再对每段调用 <see cref="ExpandPipeSegment"/>。</summary>
    public static IEnumerable<string> ExpandWordDocumentParagraphs(string paragraphs)
    {
        foreach (var part in paragraphs.Split('|', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrEmpty(part)) continue;
            foreach (var line in ExpandPipeSegment(part))
                yield return line;
        }
    }

    /// <summary>单段 <c>|</c> 之后的内容：按空行拆块，块内再按单行拆（trim，跳过空行）。</summary>
    public static IEnumerable<string> ExpandPipeSegment(string pipeSegment)
    {
        var part = pipeSegment.Trim();
        if (part.Length == 0) yield break;

        var blocks = BlankLineSplitter.Split(part);
        foreach (var rawBlock in blocks)
        {
            var block = rawBlock.Trim();
            if (block.Length == 0) continue;

            if (block.Contains('\r') || block.Contains('\n'))
            {
                foreach (var line in block.Split(NewLineSeparators, StringSplitOptions.None))
                {
                    var t = line.Trim();
                    if (t.Length > 0) yield return t;
                }
            }
            else
                yield return block;
        }
    }

    private static readonly string[] NewLineSeparators = ["\r\n", "\n", "\r"];
}
