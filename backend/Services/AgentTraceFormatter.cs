using System.Globalization;
using System.Text;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

/// <summary>agent_trace 详情截断与多行详情拼装（供单元测试覆盖）。</summary>
public static class AgentTraceFormatter
{
    public const int DefaultMaxDetailChars = 12_000;
    public const int DefaultMaxTitleChars = 240;
    public const int DefaultTextPreviewChars = 96;
    public const int DefaultMaxFunctionSampleLines = 40;

    public static string TruncateTitle(string? title, int maxChars = DefaultMaxTitleChars)
    {
        var t = (title ?? "").Trim();
        if (t.Length <= maxChars) return t;
        return t[..maxChars] + "…";
    }

    public static string TruncateDetail(string? detail, int maxChars = DefaultMaxDetailChars)
    {
        var d = detail ?? "";
        if (d.Length <= maxChars) return d;
        return d.AsSpan(0, maxChars).ToString() + "\n…(已截断)";
    }

    public static string PreviewOneLine(string? text, int maxChars = DefaultTextPreviewChars)
    {
        var t = (text ?? "").Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        if (t.Length <= maxChars) return t;
        return t[..maxChars] + "…";
    }

    public static (string Title, string Detail) BuildMemoryTrace(
        IReadOnlyList<(string Id, string Text, double Score)> sessionResults,
        IReadOnlyList<(string Id, string Text, double Score)> sharedResults,
        int sessionTopK,
        int sharedTopK)
    {
        var n = sessionResults.Count + sharedResults.Count;
        var title = $"长期记忆：命中 {n} 条（本会话 topK={sessionTopK}，共享 topK={sharedTopK}）";
        var sb = new StringBuilder();
        if (sessionResults.Count > 0)
        {
            sb.AppendLine("[本会话]");
            foreach (var r in sessionResults)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  id={r.Id}  score={r.Score:F4}  {PreviewOneLine(r.Text)}");
        }
        if (sharedResults.Count > 0)
        {
            sb.AppendLine("[共享]");
            foreach (var r in sharedResults)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  id={r.Id}  score={r.Score:F4}  {PreviewOneLine(r.Text)}");
        }
        if (n == 0)
            sb.AppendLine("检索完成，无命中条目（未向 system 注入记忆块）。");
        return (title, sb.ToString().TrimEnd());
    }

    public static (string Title, string Detail) BuildKnowledgeBaseTrace(
        string knowledgeBaseId,
        IReadOnlyList<(string Id, string Text, double Score)> results)
    {
        var title = $"知识库：命中 {results.Count} 条（knowledgeBaseId={knowledgeBaseId}）";
        var sb = new StringBuilder();
        if (results.Count == 0)
            sb.AppendLine("检索完成，无命中条目（未注入 RAG 块）。");
        else
        {
            foreach (var r in results)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  id={r.Id}  score={r.Score:F4}  {PreviewOneLine(r.Text)}");
        }
        return (title, sb.ToString().TrimEnd());
    }

    public static (string Title, string Detail) BuildDynamicToolingBootstrapTrace(int bootstrapToolCount, int catalogEntryCount)
    {
        var title = $"动态工具：首轮绑定 {bootstrapToolCount} 个函数（含 search/activate 与保底）";
        var detail = $"允许列表内可检索条目数（索引）：{catalogEntryCount}。\n"
            + "模型须先 search_available_tools 再 activate_tools，外层 Runner 会在激活后扩容工具 schema。";
        return (title, detail);
    }

    /// <summary>自动摘要成功后的 context 类 agent_trace。</summary>
    public static (string Title, string Detail) BuildContextSummarizationSuccessTrace(
        int messagesRemoved,
        int summaryCharLength,
        ContextWindowConfig ctx,
        bool historyOffloadAttempted)
    {
        var approxTurns = Math.Max(0, messagesRemoved / 2);
        var title = $"上下文：已压缩早期对话（约 {approxTurns} 轮，合并 {messagesRemoved} 条消息）";
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"触发：SummarizationEnabled，且估算 token ≥ 预算 × SummarizationTriggerRatio（当前比例={ctx.SummarizationTriggerRatio:F2}）。");
        sb.AppendLine(CultureInfo.InvariantCulture, $"操作：移除 system 之后紧接的 {messagesRemoved} 条历史消息，插入 1 条「此前对话摘要」用户消息（摘要约 {summaryCharLength} 字，配置上限 SummarizationMaxSummaryChars={ctx.SummarizationMaxSummaryChars}）。");
        if (historyOffloadAttempted)
            sb.AppendLine("若配置了 ConversationHistoryDirectory，被合并的原文会尽力追加写入该目录下的会话 md。");
        else
            sb.AppendLine("未配置会话历史落盘目录时，合并前的原文仅存在于本次摘要模型的输入中。");
        return (title, sb.ToString().TrimEnd());
    }

    /// <summary>自动摘要调用异常时的 context 类 agent_trace。</summary>
    public static (string Title, string Detail) BuildContextSummarizationFailureTrace(string friendlyReason)
    {
        var title = "上下文：历史摘要未生效";
        var detail = "已跳过压缩，继续使用完整早期消息。\n原因：" + (friendlyReason ?? "").Trim();
        return (title, detail);
    }

    /// <summary>工具参数/长消息截断后的 context 类 agent_trace。</summary>
    public static (string Title, string Detail) BuildContextTruncateTrace(
        int truncatedMessageCount,
        int keepMessages,
        int maxCharsPerMessage,
        double thresholdRatio,
        int estimatedTotalTokens,
        int historyBudgetTokens)
    {
        var title = $"上下文：已截断过长历史文本（{truncatedMessageCount} 条消息）";
        var sb = new StringBuilder();
        sb.AppendLine("触发：未走摘要分支，且估算 token ≥ 预算 × TruncateToolArgsThresholdRatio。");
        sb.AppendLine(CultureInfo.InvariantCulture, $"策略：保留最近 {keepMessages} 条消息（含索引语义与配置 TruncateToolArgsKeepMessages 一致），对其余消息中单条超过 {maxCharsPerMessage} 字符的正文截断并追加「…(已截断)」。");
        sb.AppendLine(CultureInfo.InvariantCulture, $"参数：TruncateToolArgsThresholdRatio={thresholdRatio:F4}，TruncateToolArgsMaxChars={maxCharsPerMessage}。");
        sb.AppendLine(CultureInfo.InvariantCulture, $"估算：本轮处理前 history 合计约 {estimatedTotalTokens} token，预算约 {historyBudgetTokens}（已扣 reserved）。");
        return (title, sb.ToString().TrimEnd());
    }
}
