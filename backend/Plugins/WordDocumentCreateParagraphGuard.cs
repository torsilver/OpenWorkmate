namespace OfficeCopilot.Server.Plugins;

/// <summary>拦截模型把 JSON 字符串数组等调试输出整段写入 <c>word_document_create.paragraphs</c> 的常见错误。</summary>
public static class WordDocumentCreateParagraphGuard
{
    /// <summary>高置信度启发式：整段形似 JSON 字符串数组或其它结构化转储，应拒绝写盘并提示改写为 Markdown。</summary>
    public static bool LooksLikeJsonStringArrayDump(string? paragraphs)
    {
        if (string.IsNullOrWhiteSpace(paragraphs)) return false;
        var p = paragraphs.Trim();
        // 短文本以 [ 开头多为合法说明，避免误杀
        if (p.Length < 150) return false;
        if (!p.StartsWith('[')) return false;

        // 典型 tool/模型输出：["a","b"] 或转义后的 \",\"
        if (p.Contains("\",\"", StringComparison.Ordinal)) return true;
        if (p.Contains("\"],", StringComparison.Ordinal)) return true;
        if (p.Contains("\", \"", StringComparison.Ordinal)) return true;
        if (p.Contains("[\"", StringComparison.Ordinal) && p.Contains("\"]", StringComparison.Ordinal) && p.Count(c => c == '"') >= 8)
            return true;

        return false;
    }

    public const string RejectionMessage =
        "[错误] paragraphs 疑似 JSON/程序数组字面量或结构化转储整段粘贴，无法作为 Word 正文。请先整理为自然语言，用 Markdown（| 分段、# 标题、- 列表）再调用 word_document_create；若写中文正式稿（默认按 GB/T 9704 常用归纳）请先 load_user_skill_instructions(word_cn_default_formal) 并视需要传 documentPreset=cnGovGbt9704。";
}
