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

    public static string BuildToolVectorSkipDetail(bool embeddingConfigured, bool storePersistent)
    {
        if (!embeddingConfigured)
            return "未执行向量工具检索：Embedding 未配置。";
        if (!storePersistent)
            return "未执行向量工具检索：向量存储非持久化（in-memory），按设计走两阶段子类筛选。";
        return "未执行向量工具检索。";
    }

    public static string BuildToolVectorSearchDetail(
        string? clientType,
        ToolSearchResult result,
        int topK,
        double minScore,
        int minCount,
        string decisionNote)
    {
        var sb = new StringBuilder();
        var ctLabel = string.IsNullOrWhiteSpace(clientType) ? "chrome(默认)" : clientType.Trim();
        sb.AppendLine(CultureInfo.InvariantCulture, $"clientType={ctLabel}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"参数：topK={topK}  minScore={minScore:F4}  minCount={minCount}");
        var maxScore = result.ScoredHits.Count > 0 ? result.ScoredHits[0].Score : 0.0;
        sb.AppendLine(CultureInfo.InvariantCulture, $"goodEnough={result.GoodEnough}  去重命中数={result.ScoredHits.Count}  maxScore={maxScore:F4}");
        if (result.ScoredHits.Count > 0)
        {
            sb.AppendLine("命中（分数降序）：");
            foreach (var h in result.ScoredHits)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {h.PluginName}.{h.FunctionName}  {h.Score:F4}");
        }
        else
            sb.AppendLine("无有效工具命中（解析 id 后为空）。");
        if (!string.IsNullOrWhiteSpace(decisionNote))
            sb.AppendLine(decisionNote.Trim());
        return sb.ToString().TrimEnd();
    }

    private static string MapTwoStageReason(string reasonCode) => reasonCode switch
    {
        "kernel_null" => "Kernel 为空，使用全量工具。",
        "no_functions" => "Kernel 中无函数，使用全量工具。",
        "no_subcategories" => "无可用子类列表，使用全量工具。",
        "stage1_fallback" => "一阶段子类选择返回「全部」或失败，使用全量工具。",
        "no_candidates_after_stage1" => "一阶段选中的子类下无候选函数，使用全量工具。",
        "ok_two_stage" => "已采用两阶段路径：子类 → 合并 AlwaysInclude 后的函数集。",
        _ => $"reasonCode={reasonCode}"
    };

    public static (string Title, string Detail) BuildTwoStageToolTrace(ToolSelectionOutcome outcome, int maxFunctionLines = DefaultMaxFunctionSampleLines)
    {
        var title = outcome.SelectedPairs is { Count: > 0 }
            ? $"工具选择：两阶段，合并后 {outcome.MergedFunctionCount} 个 (插件,函数) 对"
            : "工具选择：两阶段未收窄，使用全量工具";
        var sb = new StringBuilder();
        sb.AppendLine(MapTwoStageReason(outcome.ReasonCode));
        if (outcome.SelectedSubcategoryIds is { Count: > 0 })
            sb.AppendLine("一阶段子类 id：" + string.Join(", ", outcome.SelectedSubcategoryIds));
        sb.AppendLine(CultureInfo.InvariantCulture, $"候选函数数（子类并集）：{outcome.CandidateFunctionCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"合并 AlwaysInclude 后：(插件,函数) 对数 = {outcome.MergedFunctionCount}");
        if (outcome.SelectedPairs is { Count: > 0 })
        {
            sb.AppendLine("函数列表（截断）：");
            var n = 0;
            foreach (var p in outcome.SelectedPairs)
            {
                if (n++ >= maxFunctionLines)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  … 其余 {outcome.SelectedPairs.Count - maxFunctionLines} 条省略");
                    break;
                }
                sb.AppendLine($"  {p.PluginName}.{p.FunctionName}");
            }
        }
        return (title, sb.ToString().TrimEnd());
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
