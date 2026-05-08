using System.Collections.Concurrent;

namespace OpenWorkmate.Server.Services.OpenAiCompat;

/// <summary>
/// 按会话缓存各轮「带 tool_calls 的 assistant」流式响应累积的 <c>reasoning_content</c> 全文（按时间顺序追加），供出站 HTTP 请求按轮次索引注入。
/// </summary>
internal static class OpenAiReasoningEchoStore
{
    private static readonly ConcurrentDictionary<string, List<string>> Pending =
        new(StringComparer.Ordinal);

    /// <summary>每完成一轮含 tool_calls 的 assistant SSE，追加一条推理全文。</summary>
    internal static void AppendAfterToolAssistantRound(string? sessionId, string accumulatedReasoning)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        var list = Pending.GetOrAdd(sessionId, _ => new List<string>());
        lock (list)
        {
            var payload = string.IsNullOrWhiteSpace(accumulatedReasoning) ? " " : accumulatedReasoning;
            list.Add(payload);
        }
    }

    /// <summary>线程安全快照，供出站 patch 按 assistant+tool 次序索引读取。</summary>
    internal static IReadOnlyList<string> SnapshotReasonings(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<string>();
        if (!Pending.TryGetValue(sessionId, out var list))
            return Array.Empty<string>();
        lock (list)
        {
            return list.Count == 0 ? Array.Empty<string>() : list.ToArray();
        }
    }

    /// <summary>仅供诊断：当前会话已缓存的 assistant+tool 推理轮数。</summary>
    internal static int GetSessionReasoningRoundCount(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;
        if (!Pending.TryGetValue(sessionId, out var list))
            return 0;
        lock (list)
            return list.Count;
    }
}
