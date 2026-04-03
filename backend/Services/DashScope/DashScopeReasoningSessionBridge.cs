using System.Collections.Concurrent;

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>
/// 将百炼 SSE 旁路解析出的推理片段与 <see cref="ChatService"/> 的 drain 对齐（仅 UI 流式，不入历史、不参与业务判断）。
/// HttpClient Handler 与 SK 主循环可能不在同一 <see cref="System.Threading.AsyncLocal{T}"/> 复制上，
/// 因此不能仅靠 <see cref="DashScopeReasoningContext.DrainCurrentFrame"/>；按 <see cref="SessionContext"/> 的 sessionId 登记队列。
/// </summary>
internal static class DashScopeReasoningSessionBridge
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> SessionToQueue = new(StringComparer.Ordinal);

    /// <summary>在 PushFrame 之后调用；将后续闭包 Enqueue 的队列绑定到当前会话。</summary>
    internal static void AttachQueue(string? sessionId, ConcurrentQueue<string> queue)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        SessionToQueue[sessionId] = queue;
    }

    /// <summary>流结束 PopFrame 时调用；仅移除仍指向本队列的登记，避免误删下一轮。</summary>
    internal static void TryDetachQueue(string? sessionId, ConcurrentQueue<string> queue)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        SessionToQueue.TryGetValue(sessionId, out var current);
        if (ReferenceEquals(current, queue))
            SessionToQueue.TryRemove(sessionId, out _);
    }

    /// <summary>与 Chat 流式轮次同一 sessionId：FIFO drain，不结束帧（帧由 Handler PopFrame）。</summary>
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
