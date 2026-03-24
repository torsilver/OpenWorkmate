using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server;

/// <summary>Stores WebSocket, optional client type (chrome | office-word | office-excel | office-powerpoint | wps), and optional display name (e.g. page title) per session.</summary>
public sealed class SessionManager
{
    private readonly ILogger<SessionManager> _logger;
    private readonly ConcurrentDictionary<string, SessionEntry> _connections = new();

    public SessionManager(ILogger<SessionManager> logger) => _logger = logger;

    public void Add(string sessionId, WebSocket ws, string? clientType = null) =>
        _connections[sessionId] = new SessionEntry(ws, clientType, null);

    public void Remove(string sessionId) => _connections.TryRemove(sessionId, out _);

    public WebSocket? Get(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.WebSocket : null;

    /// <summary>Gets the client type for the session, if any (e.g. chrome, office-word, office-excel, office-powerpoint, wps).</summary>
    public string? GetClientType(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.ClientType : null;

    /// <summary>Sets the display name (e.g. Agent name / page title) for the session.</summary>
    public void SetDisplayName(string sessionId, string? displayName)
    {
        if (_connections.TryGetValue(sessionId, out var entry))
            _connections[sessionId] = entry with { DisplayName = displayName };
    }

    /// <summary>Gets the display name for the session, if any.</summary>
    public string? GetDisplayName(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.DisplayName : null;

    public int Count => _connections.Count;

    public IReadOnlyCollection<string> ActiveSessions => _connections.Keys.ToList();

    /// <summary>获取指定 clientType 的所有 sessionId（用于跨 Agent 任务推送）。</summary>
    public IReadOnlyList<string> GetSessionIdsByClientType(string clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)) return Array.Empty<string>();
        var ct = clientType.Trim();
        return _connections
            .Where(kv => string.Equals(kv.Value.ClientType, ct, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
    }

    public async Task SendToAsync(string sessionId, string message)
    {
        if (_connections.TryGetValue(sessionId, out var entry) && entry.WebSocket.State == WebSocketState.Open)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await entry.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    /// <summary>向当前所有已打开的连接广播 UTF-8 JSON 文本；单连接失败不影响其它连接。</summary>
    public async Task BroadcastToAllOpenAsync(string utf8Json, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(utf8Json);
        foreach (var kv in _connections)
        {
            var ws = kv.Value.WebSocket;
            if (ws.State != WebSocketState.Open) continue;
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Broadcast skip session {SessionId} (send failed)", kv.Key);
            }
        }
    }

    private sealed record SessionEntry(WebSocket WebSocket, string? ClientType, string? DisplayName);
}
