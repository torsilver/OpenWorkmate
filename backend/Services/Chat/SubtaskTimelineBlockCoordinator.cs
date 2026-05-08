using System.Collections.Concurrent;

namespace OpenWorkmate.Server.Services.Chat;

/// <summary>
/// 子任务（<c>run_subtask</c>）内 <c>reasoning_chunk</c> 的 blockSeq/blockKind，与主会话
/// <see cref="TimelineBlockStreamCoordinator"/> 隔离，避免与主时间线 Map 键冲突。
/// </summary>
public sealed class SubtaskTimelineBlockCoordinator
{
    public const string KindThink = "think";
    public const string KindAnswer = "answer";

    private sealed class State
    {
        public readonly object Gate = new();
        public string? ActiveKind;
        public int BlockSeq = -1;
    }

    private readonly ConcurrentDictionary<string, State> _map = new(StringComparer.Ordinal);

    public void BeginSubtaskRun(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var s = _map.GetOrAdd(sessionId, _ => new State());
        lock (s.Gate)
        {
            s.ActiveKind = null;
            s.BlockSeq = -1;
        }
    }

    public void OnToolInvocationStart(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (!_map.TryGetValue(sessionId, out var s)) return;
        lock (s.Gate)
        {
            s.ActiveKind = null;
        }
    }

    public void EndSubtaskRun(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _map.TryRemove(sessionId, out _);
    }

    public (int blockSeq, string blockKind) EnsureChunkBlock(string sessionId, string requestedKind)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId required", nameof(sessionId));
        if (requestedKind != KindThink && requestedKind != KindAnswer)
            throw new ArgumentException("kind must be think or answer", nameof(requestedKind));

        var s = _map.GetOrAdd(sessionId, _ => new State());
        lock (s.Gate)
        {
            if (s.ActiveKind != requestedKind)
            {
                s.BlockSeq++;
                s.ActiveKind = requestedKind;
            }

            return (s.BlockSeq, requestedKind);
        }
    }
}
