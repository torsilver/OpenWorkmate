using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OfficeCopilot.Server;

/// <summary>Stores WebSocket and optional client type (chrome | office-word | office-excel | wps) per session.</summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionEntry> _connections = new();

    public void Add(string sessionId, WebSocket ws, string? clientType = null) =>
        _connections[sessionId] = new SessionEntry(ws, clientType);

    public void Remove(string sessionId) => _connections.TryRemove(sessionId, out _);

    public WebSocket? Get(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.WebSocket : null;

    /// <summary>Gets the client type for the session, if any (e.g. chrome, office-word, office-excel, wps).</summary>
    public string? GetClientType(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.ClientType : null;

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
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            await entry.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private sealed record SessionEntry(WebSocket WebSocket, string? ClientType);
}
