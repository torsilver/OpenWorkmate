using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>上下文长度错误检测与为重试裁剪历史。</summary>
public static class ContextLengthRetryHelper
{
    public static bool IsContextLengthError(Exception ex)
    {
        var msg = (ex.Message ?? "").ToLowerInvariant();
        return msg.Contains("context_length") || msg.Contains("maximum context") || msg.Contains("token limit")
            || msg.Contains("too many tokens");
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
            var removed = TokenEstimator.EstimateTokens(history[1].Text ?? "", ctx);
            history.RemoveAt(1);
            total -= removed;
        }
    }
}
