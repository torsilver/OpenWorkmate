using System.Collections.Concurrent;
using System.Threading;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 主会话单轮内统计「真实进入插件执行」次数（与 <see cref="OfficeCopilot.Server.Filters.ToolStatusFilter"/> 对齐）。
/// 以 <see cref="SessionContext"/> 的 sessionId 为键存入 <see cref="ConcurrentDictionary{TKey,TValue}"/>，
/// 避免流式对话、并行工具调用（如一次响应内多个 function call）时 <see cref="System.Threading.AsyncLocal{T}"/> 与执行上下文不一致导致计数始终为 0。
/// </summary>
public static class ToolInvocationTurnMeter
{
    private sealed class TurnState
    {
        internal volatile bool Active = true;
        internal int Count;
    }

    private static readonly ConcurrentDictionary<string, TurnState> BySession = new(StringComparer.Ordinal);

    public static void BeginTurn(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        BySession[sessionId.Trim()] = new TurnState();
    }

    public static void EndTurn(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        if (BySession.TryRemove(sessionId.Trim(), out var h))
            h.Active = false;
    }

    /// <summary>在进入内核函数体之前调用（由 ToolStatusFilter 调用）。</summary>
    public static void RecordInvocation()
    {
        var sid = SessionContext.GetSessionId();
        if (string.IsNullOrWhiteSpace(sid))
            return;
        if (!BySession.TryGetValue(sid.Trim(), out var h) || !h.Active)
            return;
        Interlocked.Increment(ref h.Count);
    }

    public static int GetCount(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return 0;
        return BySession.TryGetValue(sessionId.Trim(), out var h) && h.Active
            ? Volatile.Read(ref h.Count)
            : 0;
    }

    /// <summary>同一轮内第二次模型请求前清零，以便分别统计首轮/重试轮工具次数。</summary>
    public static void ResetCount(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        if (BySession.TryGetValue(sessionId.Trim(), out var h) && h.Active)
            Interlocked.Exchange(ref h.Count, 0);
    }
}
