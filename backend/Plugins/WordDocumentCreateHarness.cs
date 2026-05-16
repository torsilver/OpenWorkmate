using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenWorkmate.Server.Plugins;

/// <summary>
/// <see cref="WordPlugin.WordDocumentCreate"/> 落盘前的输入马鞍：拦截「疑似嵌入的 JSON 字符串数组字面量」且无法被
/// <see cref="WordDocumentCreateParagraphsParser"/> 消化为多条 Markdown 段的高置信残留，避免整段写进 Word。
/// </summary>
/// <remarks>
/// 启发式（收窄误伤）：
/// <list type="number">
/// <item>仅检查 Trim 后长度 ≥ <see cref="MinSuspiciousLength"/> 的项，避免误杀「[1] 说明」等短句。</item>
/// <item>首字符为 <c>[</c>，且满足以下任一，视为「疑似 JSON 数组字面量」：含 <c>","</c>；或含字符串后接逗号的子串 <c>",</c>（如 <c>["a",1]</c>）；或以 <c>]</c> 结尾且长度 ≥ <see cref="LongBracketLiteralMinLength"/>。</item>
/// <item>对疑似项：<c>JsonDocument.Parse</c> 失败、根非数组、或任一数组成员非字符串 → 整次调用拒绝（不创建/不覆盖文件）。</item>
/// <item>若解析为「全字符串数组」且通过上述检查，则放行（与计划一致；正常路径下解析器应已展开，此项极少触发）。</item>
/// </list>
/// </remarks>
internal static class WordDocumentCreateHarness
{
    /// <summary>短于此长度的 <c>[</c> 开头项不进入 JSON 字面量检测。</summary>
    private const int MinSuspiciousLength = 12;

    /// <summary>无 <c>","</c> 但整段仍以 <c>]</c> 结尾且足够长时，仍视为疑似数组字面量（如单元素长串）。</summary>
    private const int LongBracketLiteralMinLength = 80;

    /// <summary>
    /// 校验 <see cref="WordDocumentCreateParagraphsParser.Parse"/> 之后的段落列表。
    /// </summary>
    /// <returns><c>true</c> 表示通过；<c>false</c> 时 <paramref name="rejectionMessage"/> 为带 <c>[无效]</c> 前缀的说明。</returns>
    public static bool TryValidateParsedParagraphs(string[]? segments, [NotNullWhen(false)] out string? rejectionMessage)
    {
        rejectionMessage = null;
        if (segments is null || segments.Length == 0)
            return true;

        for (var i = 0; i < segments.Length; i++)
        {
            var raw = segments[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var t = raw.Trim();
            if (t.Length < MinSuspiciousLength || t[0] != '[')
                continue;

            if (!LooksLikeJsonArrayLiteral(t))
                continue;

            if (!TryValidateJsonStringArrayContent(t, i + 1, out rejectionMessage))
                return false;
        }

        return true;
    }

    private static bool LooksLikeJsonArrayLiteral(string t)
    {
        if (t.Contains("\",\"", StringComparison.Ordinal))
            return true;
        // 字符串与下一元素之间的 `",`（如 ["a",1]），模型输出里比 `","` 更常见
        if (t.Contains("\",", StringComparison.Ordinal))
            return true;
        if (t.Length >= LongBracketLiteralMinLength && t.EndsWith("]", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static bool TryValidateJsonStringArrayContent(string t, int oneBasedIndex, [NotNullWhen(false)] out string? rejectionMessage)
    {
        rejectionMessage = null;
        try
        {
            using var doc = JsonDocument.Parse(t);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                rejectionMessage = BuildRejection(
                    oneBasedIndex,
                    "根节点不是 JSON 数组。",
                    "请使用工具参数的顶层 JSON 数组，每项为一段 Markdown 正文字符串。");
                return false;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    rejectionMessage = BuildRejection(
                        oneBasedIndex,
                        "JSON 数组中含有非字符串元素；paragraphs 的每一项应为 Markdown 字符串，勿嵌套对象或数字数组。",
                        "请改为 string[]，例如 [\"## 概述\\n……\", \"## 要点\\n……\"]；勿把整段 [\"…\",\"…\"] 作为单个字符串塞进数组。");
                    return false;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            rejectionMessage = BuildRejection(
                oneBasedIndex,
                $"JSON 解析失败（{ex.Message}）。常见原因：正文内英文双引号未按 JSON 转义为 \\\"。",
                "请改为顶层 string[] 多项，或修正转义后重试；勿将未转义的网页/API 抄录直接塞进 JSON 字符串。");
            return false;
        }
    }

    private static string BuildRejection(int oneBasedIndex, string reason, string hint)
    {
        return $"[无效] word_document_create：paragraphs 第 {oneBasedIndex} 项疑似整段 JSON 数组字面量但未通过校验。{reason} {hint}";
    }
}
