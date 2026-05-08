using System.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// 实验向：在 MAF Compaction 之前，按「当前用户句拆词 vs 可删区最旧消息」的简单重叠度，
/// 在历史 token 接近预算时删除与当前句无词重叠的最旧可删消息（遇首条有重叠则停止）。
/// 与 <see cref="CompactionRelevanceDiagnostics"/> 使用同类拆词逻辑，便于对照日志评估策略。
/// </summary>
public static class CompactionQueryAwareHeuristic
{
    /// <summary>
    /// 尝试删除若干条「最旧可删且与当前用户句词重叠为 0」的消息。
    /// </summary>
    /// <returns>实际删除条数。</returns>
    public static int TryTrimLowRelevanceOldestRemovable(
        List<ChatMessage> history,
        string? userMessage,
        ContextWindowConfig ctx,
        SessionConfig session,
        int effectiveMaxContextTokens,
        ILogger logger,
        string sessionId,
        string roundId)
    {
        if (!ctx.CompactionQueryAwareHeuristicEnabled || history.Count < 3)
            return 0;

        var budget = effectiveMaxContextTokens - ctx.ReservedOutputTokens;
        if (budget <= 0)
            return 0;

        var totalTokens = ContextManager.EstimateHistoryTokens(history, ctx);
        var pressureThreshold = budget * ctx.CompactionQueryAwareTokenPressureRatio;
        if (totalTokens <= pressureThreshold)
            return 0;

        var terms = BuildTerms(userMessage);
        if (terms.Length == 0)
            return 0;

        var minMessagesToKeep = 1 + Math.Max(0, session.MinTurnsToKeep) * 2;
        var maxRemovals = Math.Max(0, ctx.CompactionQueryAwareMaxRemovalsPerTurn);
        var removed = 0;

        while (removed < maxRemovals && history.Count > minMessagesToKeep)
        {
            var start = ConversationCompactBoundary.GetFirstRemovableChatIndex(history);
            if (start >= history.Count)
                break;
            var text = history[start].Text ?? "";
            if (text.Length == 0)
            {
                history.RemoveAt(start);
                removed++;
                continue;
            }

            if (OverlapScore(text, terms) > 0)
                break;

            history.RemoveAt(start);
            removed++;
        }

        if (removed > 0)
        {
            logger.LogInformation(
                "[{SessionId}] [{RoundId}] CompactionQueryAware: removed={Removed} lowRelevanceOldest (estimatedHistoryTokens={Total} pressureThreshold≈{Pressure:F0})",
                sessionId, roundId, removed, totalTokens, pressureThreshold);
        }

        return removed;
    }

    internal static string[] BuildTerms(string? userMessage)
    {
        return (userMessage ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .Take(24)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static int OverlapScore(string text, string[] terms)
    {
        if (text.Length == 0 || terms.Length == 0)
            return 0;
        var span = text.AsSpan();
        var score = 0;
        foreach (var t in terms)
        {
            if (span.Contains(t, StringComparison.OrdinalIgnoreCase))
                score++;
        }

        return score;
    }
}
