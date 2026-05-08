using System.Collections.Concurrent;

namespace OpenWorkmate.Server.Services.OpenAiCompat;

/// <summary>
/// SSE 旁路解析出的顶层 <c>usage</c> JSON 片段与 MAF 主循环 drain 对齐（仅 UI，不入会话历史）。
/// </summary>
internal static class OpenAiStreamUsageSessionBridge
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> SessionToQueue =
        new(StringComparer.Ordinal);

    internal static void AttachQueue(string? sessionId, ConcurrentQueue<string> queue)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        SessionToQueue[sessionId] = queue;
    }

    internal static void TryDetachQueue(string? sessionId, ConcurrentQueue<string> queue)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        SessionToQueue.TryGetValue(sessionId, out var current);
        if (ReferenceEquals(current, queue))
            SessionToQueue.TryRemove(sessionId, out _);
    }

    internal static IEnumerable<string> DrainForSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            yield break;
        if (!SessionToQueue.TryGetValue(sessionId, out var q))
            yield break;
        while (q.TryDequeue(out var s))
        {
            if (!string.IsNullOrEmpty(s))
                yield return s;
        }
    }
}
