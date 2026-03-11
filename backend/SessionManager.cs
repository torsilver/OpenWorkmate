using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OfficeCopilot.Server;

public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public void Add(string sessionId, WebSocket ws) => _connections[sessionId] = ws;

    public void Remove(string sessionId) => _connections.TryRemove(sessionId, out _);

    public WebSocket? Get(string sessionId) =>
        _connections.TryGetValue(sessionId, out var ws) ? ws : null;

    public int Count => _connections.Count;

    public IReadOnlyCollection<string> ActiveSessions => _connections.Keys.ToList();

    public async Task SendToAsync(string sessionId, string message)
    {
        if (_connections.TryGetValue(sessionId, out var ws) && ws.State == WebSocketState.Open)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
