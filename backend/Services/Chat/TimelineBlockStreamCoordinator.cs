using System.Collections.Concurrent;

namespace OfficeCopilot.Server.Services.Chat;

/// <summary>
/// 每会话、每轮 stream_start 起的推理/正文时间线逻辑段序号（与 WS reasoning_chunk / stream_chunk 的 blockSeq、blockKind 一致）。
/// 工具开始时清空活动段类型，使工具后续新一轮正文/推理获得新 blockSeq。
/// </summary>
public sealed class TimelineBlockStreamCoordinator
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

    public void BeginRound(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var s = _map.GetOrAdd(sessionId, _ => new State());
        lock (s.Gate)
        {
            s.ActiveKind = null;
            s.BlockSeq = -1;
        }
    }

    /// <summary>在推送 tool_invocation_start 时调用，使后续正文/推理开启新段。</summary>
    public void OnToolInvocationStart(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (!_map.TryGetValue(sessionId, out var s)) return;
        lock (s.Gate)
        {
            s.ActiveKind = null;
        }
    }

    public void EndRound(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _map.TryRemove(sessionId, out _);
    }

    /// <summary>为即将发送的 chunk 解析块序号与类型（think / answer）。</summary>
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
