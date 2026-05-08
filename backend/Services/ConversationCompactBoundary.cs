using System.Globalization;
using Microsoft.Extensions.AI;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// 对话压缩边界：摘要消息内嵌机器可解析标记，裁剪历史时保护摘要及其后一条锚点消息。
/// </summary>
public static class ConversationCompactBoundary
{
    public const string SummaryPrefix = "[此前对话摘要]";

    /// <summary>紧接前缀后的说明：标明摘要性质，避免模型把压缩块当作磁盘文件当前状态。</summary>
    public const string SummaryScopeNotice =
        "以下块内为压缩后的历史对话摘要，可能已过时，不能单独作为本机 Word/Excel/PPT 等文件的当前状态依据；核对文件实际内容须重新调用读类工具。";

    public const string SummaryXmlOpen = "<prior_conversation_summary>";
    public const string SummaryXmlClose = "</prior_conversation_summary>";

    /// <summary>单行标记，形如 <c>[compact_boundary:2026-04-01T12:00:00.000Z]</c>。</summary>
    public const string BoundaryMarkerPrefix = "[compact_boundary:";

    public static string BuildSummaryMessageBody(string summaryBody, DateTimeOffset compactUtc)
    {
        var iso = compactUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return $"{SummaryPrefix}\n{SummaryScopeNotice}\n{SummaryXmlOpen}\n{summaryBody}\n{SummaryXmlClose}\n{BoundaryMarkerPrefix}{iso}]";
    }

    public static bool MessageContainsCompactBoundary(ChatMessage msg) =>
        (msg.Text ?? "").Contains(BoundaryMarkerPrefix, StringComparison.Ordinal);

    /// <summary>自索引 1 起第一条含压缩边界的消息索引；无则 -1。</summary>
    public static int FindCompactSummaryMessageIndex(IList<ChatMessage> history)
    {
        for (var i = 1; i < history.Count; i++)
        {
            if (MessageContainsCompactBoundary(history[i]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 裁剪「最旧对话」时可删除的第一条消息索引（≥1）。无压缩块时为 1；
    /// 有压缩块时为「摘要 + 其后一条锚点」之后的索引；若仅余 system+摘要则返回 history.Count（表示不可再删对话消息）。
    /// </summary>
    public static int GetFirstRemovableChatIndex(IList<ChatMessage> history)
    {
        var idx = FindCompactSummaryMessageIndex(history);
        if (idx < 0)
            return 1;
        if (history.Count <= idx + 1)
            return history.Count;
        return idx + 2;
    }
}
