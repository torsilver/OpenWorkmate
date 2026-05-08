using System.Collections.Concurrent;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// Per-session cancellation for the current chat stream. When the client sends "stop",
/// we cancel the session's CTS so StreamChatAsync stops.
/// </summary>
public sealed class StreamCancelService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _bySession = new();

    /// <summary>
    /// Creates a new CancellationTokenSource for the session (disposes any existing one),
    /// stores it, and returns the token to pass to StreamChatAsync.
    /// </summary>
    public CancellationToken CreateForSession(string sessionId)
    {
        Remove(sessionId);
        var cts = new CancellationTokenSource();
        _bySession[sessionId] = cts;
        return cts.Token;
    }

    /// <summary>
    /// Cancels the current stream for the session, if any.
    /// </summary>
    public void Cancel(string sessionId)
    {
        if (_bySession.TryGetValue(sessionId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Removes and disposes the session's CTS. Call from HandleChatStream finally.
    /// </summary>
    public void Remove(string sessionId)
    {
        if (_bySession.TryRemove(sessionId, out var cts))
        {
            try { cts.Dispose(); } catch { }
        }
    }
}
