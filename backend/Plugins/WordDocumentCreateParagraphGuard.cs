namespace OfficeCopilot.Server.Plugins;

/// <summary>拦截模型在 <c>word_document_create</c> 的 <c>paragraphs</c>（<c>string[]</c> 归并后）中仍整段粘贴 JSON 字符串数组或结构化调试转储的常见错误。</summary>
public static class WordDocumentCreateParagraphGuard
{
    /// <summary>高置信度启发式：归一化后的正文串形似 JSON 字符串数组或其它结构化转储，应拒绝写盘并提示改写为 Markdown 分项。</summary>
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
        "[错误] paragraphs（string[]）的某项或合并结果仍疑似 JSON/程序数组字面量或结构化转储，无法作为 Word 正文。请改为多项自然语言字符串（每项用 Markdown：# 标题、- 列表，项内可用 | 或空行）；不要把整段 [\"…\",\"…\"] 塞进单个数组元素。若写中文正式稿请先 load_user_skill_instructions(word_cn_default_formal) 并视需要传 documentPreset=cnGovGbt9704。";
}
