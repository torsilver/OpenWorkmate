using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// 实验向：在调用 MAF Compaction 前，按「用户句拆词 vs 各条消息文本」的简单重叠度打日志，便于评估 Query-aware 摘要策略；不改变历史内容。
/// 启用：环境变量 <c>OpenWorkmate_COMPACTION_RELEVANCE_LOG=1</c>。
/// </summary>
public static class CompactionRelevanceDiagnostics
{
    public static void LogIfEnabled(
        ILogger logger,
        string sessionId,
        string roundId,
        string userMessage,
        IReadOnlyList<ChatMessage> history)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("OpenWorkmate_COMPACTION_RELEVANCE_LOG"), "1", StringComparison.Ordinal))
            return;

        var terms = (userMessage ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .Take(24)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
        {
            logger.LogInformation(
                "[{SessionId}] [{RoundId}] CompactionRelevance: no terms from user message; skip overlap.",
                sessionId, roundId);
            return;
        }

        var top = new List<(int Index, int Score)>();
        for (var i = 0; i < history.Count; i++)
        {
            var text = history[i].Text ?? "";
            if (text.Length == 0) continue;
            var span = text.AsSpan();
            var score = 0;
            foreach (var t in terms)
            {
                if (span.Contains(t, StringComparison.OrdinalIgnoreCase))
                    score++;
            }

            top.Add((i, score));
        }

        top.Sort((a, b) => b.Score.CompareTo(a.Score));
        var preview = string.Join(",", top.Take(6).Select(x => $"{x.Index}:{x.Score}"));
        logger.LogInformation(
            "[{SessionId}] [{RoundId}] CompactionRelevance: termCount={N}, topIndexScores=[{Preview}]",
            sessionId, roundId, terms.Length, preview);
    }
}
