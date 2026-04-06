using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 上下文管理：历史裁剪、截断、摘要、注入等逻辑的统一入口。
/// 从 ChatService 中提取出来，便于独立测试和复用。
/// </summary>
public sealed class ContextManager
{
    private readonly ConfigService _configService;
    private readonly ILogger<ContextManager> _logger;

    public ContextManager(ConfigService configService, ILogger<ContextManager> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>获取当前生效的上下文 token 上限。</summary>
    public int GetEffectiveMaxContextTokens(AiModelEntry? activeEntry)
    {
        if (activeEntry?.ContextLength is > 0)
            return activeEntry.ContextLength.Value;
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        return ctx.MaxContextTokens > 0 ? ctx.MaxContextTokens : 64_000;
    }

    /// <summary>按轮数和 token 预算裁剪历史。</summary>
    public void TrimHistory(List<ChatMessage> history, AiModelEntry? activeEntry)
    {
        var session = _configService.Current.Session ?? new SessionConfig();
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        var maxMessagesByTurns = 1 + session.MaxHistoryTurns * 2;

        while (history.Count > maxMessagesByTurns)
        {
            var removeAt = ConversationCompactBoundary.GetFirstRemovableChatIndex(history);
            if (removeAt >= history.Count)
                break;
            history.RemoveAt(removeAt);
        }

        if (ctx.PassThroughContext)
            return;

        var maxContextTokens = GetEffectiveMaxContextTokens(activeEntry);
        if (maxContextTokens <= 0)
            return;

        var budget = maxContextTokens - ctx.ReservedOutputTokens;
        if (budget <= 0)
            return;

        var totalTokens = EstimateHistoryTokens(history, ctx);
        var minMessagesToKeep = 1 + Math.Max(0, session.MinTurnsToKeep) * 2;
        while (totalTokens > budget && history.Count > minMessagesToKeep)
        {
            if (history.Count <= 2)
                break;
            var start = ConversationCompactBoundary.GetFirstRemovableChatIndex(history);
            if (start >= history.Count)
                break;
            var removed = EstimateMessageTokens(history[start], ctx);
            history.RemoveAt(start);
            totalTokens -= removed;
            if (totalTokens <= budget || history.Count <= minMessagesToKeep)
                continue;
            if (start >= history.Count)
                continue;
            removed = EstimateMessageTokens(history[start], ctx);
            history.RemoveAt(start);
            totalTokens -= removed;
        }
    }

    /// <summary>估算整个历史的 token 总数，含图片的视觉 token 估算。</summary>
    public static int EstimateHistoryTokens(IList<ChatMessage> history, ContextWindowConfig ctx)
    {
        var total = 0;
        for (var i = 0; i < history.Count; i++)
            total += EstimateMessageTokens(history[i], ctx);
        return total;
    }

    /// <summary>估算单条消息的 token 数，含图片的视觉 token 估算。</summary>
    public static int EstimateMessageTokens(ChatMessage msg, ContextWindowConfig ctx)
    {
        var tokens = TokenEstimator.EstimateTokens(msg.Text ?? "", ctx);
        if (msg.Contents is { Count: > 0 })
        {
            foreach (var item in msg.Contents)
            {
                if (item is TextContent text)
                    tokens += TokenEstimator.EstimateTokens(text.Text ?? "", ctx);
                else if (item is DataContent)
                    tokens += TokenEstimator.EstimateImageTokens(1024, 1024);
            }
        }
        return tokens;
    }

    /// <summary>为 context_length 重试裁剪历史：先按轮数限制，再按预算减半裁剪。</summary>
    public static void TrimHistoryForRetry(List<ChatMessage> history, int maxTurns, ContextWindowConfig ctx)
    {
        var keepMessages = 1 + Math.Max(0, maxTurns) * 2;
        while (history.Count > keepMessages)
            history.RemoveAt(1);
        var halfBudget = (ctx.MaxContextTokens - ctx.ReservedOutputTokens) / 2;
        if (halfBudget <= 0) return;
        var total = 0;
        for (var i = 0; i < history.Count; i++)
            total += TokenEstimator.EstimateTokens(history[i].Text ?? "", ctx);
        while (total > halfBudget && history.Count > 3)
        {
            var start = ConversationCompactBoundary.GetFirstRemovableChatIndex(history);
            if (start >= history.Count)
                break;
            var removed = TokenEstimator.EstimateTokens(history[start].Text ?? "", ctx);
            history.RemoveAt(start);
            total -= removed;
        }
    }

    public static bool IsContextLengthError(Exception ex)
    {
        var msg = (ex.Message ?? "").ToLowerInvariant();
        return msg.Contains("context_length") || msg.Contains("maximum context") || msg.Contains("token limit")
            || msg.Contains("too many tokens");
    }
}
