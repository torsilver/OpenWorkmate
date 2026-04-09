using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server;

/// <summary>WebSocket 会话：clientType、Agent 身份（来自配置 + WS query）、页签标题（仅页面上下文）。</summary>
public sealed class SessionManager
{
    private readonly ILogger<SessionManager> _logger;
    private readonly ConcurrentDictionary<string, SessionEntry> _connections = new();

    public SessionManager(ILogger<SessionManager> logger) => _logger = logger;

    /// <summary>Registers or replaces a session. If the same <paramref name="sessionId"/> reconnects, the previous entry's send lock is disposed.</summary>
    public void Add(string sessionId, WebSocket ws, string? clientType, string agentProfileId, string agentDisplayName)
    {
        if (_connections.TryRemove(sessionId, out var old))
        {
            try { old.SendLock.Dispose(); }
            catch (ObjectDisposedException) { /* ignore */ }
        }

        var pid = (agentProfileId ?? "").Trim();
        var dn = (agentDisplayName ?? "").Trim();
        _connections[sessionId] = new SessionEntry(ws, clientType, pid, dn, null, new SemaphoreSlim(1, 1));
    }

    public void Remove(string sessionId)
    {
        if (_connections.TryRemove(sessionId, out var entry))
        {
            try { entry.SendLock.Dispose(); }
            catch (ObjectDisposedException) { /* ignore */ }
        }
    }

    public WebSocket? Get(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.WebSocket : null;

    /// <summary>True if the session exists and its socket is <see cref="WebSocketState.Open"/>.</summary>
    public bool IsSessionWebSocketOpen(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) && entry.WebSocket.State == WebSocketState.Open;

    /// <summary>Gets the client type for the session, if any (e.g. chrome, office-word, office-excel, office-powerpoint, wps).</summary>
    public string? GetClientType(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.ClientType : null;

    /// <summary>当前 WS 绑定的 Agent 配置 Id（握手时解析）。</summary>
    public string? GetAgentProfileId(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry)
            ? (string.IsNullOrEmpty(entry.AgentProfileId) ? null : entry.AgentProfileId)
            : null;

    /// <summary>Agent 展示名（记忆/计划归属）；非页签标题。</summary>
    public string? GetDisplayName(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry)
            ? (string.IsNullOrEmpty(entry.AgentDisplayName) ? null : entry.AgentDisplayName)
            : null;

    /// <summary>set_context 更新的当前页标题（日志/调试）。</summary>
    public void SetPageContextTitle(string sessionId, string? pageTitle)
    {
        if (_connections.TryGetValue(sessionId, out var entry))
            _connections[sessionId] = entry with { PageContextTitle = pageTitle };
    }

    /// <summary>当前页标题（若有）。</summary>
    public string? GetPageContextTitle(string sessionId) =>
        _connections.TryGetValue(sessionId, out var entry) ? entry.PageContextTitle : null;

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

    /// <summary>Serializes and sends a frame on the session's socket. All sends for that session are serialized (no concurrent <c>SendAsync</c>).</summary>
    public Task SendWsMessageAsync(string sessionId, WsMessage msg, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(sessionId, out var entry))
            return Task.CompletedTask;
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        return SendUtf8JsonLockedAsync(entry, json, sessionId, msg.Type ?? "?", cancellationToken);
    }

    /// <summary>Sends pre-serialized UTF-8 JSON text. Serialized with other sends on the same session.</summary>
    public Task SendToAsync(string sessionId, string message, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(sessionId, out var entry))
            return Task.CompletedTask;
        return SendUtf8JsonLockedAsync(entry, message, sessionId, "SendToAsync", cancellationToken);
    }

    /// <summary>向当前所有已打开的连接广播 UTF-8 JSON 文本；单连接失败不影响其它连接。每个会话内与其它出站消息共用发送锁。</summary>
    public async Task BroadcastToAllOpenAsync(string utf8Json, CancellationToken cancellationToken = default)
    {
        foreach (var kv in _connections)
        {
            if (kv.Value.WebSocket.State != WebSocketState.Open) continue;
            try
            {
                await SendUtf8JsonLockedAsync(kv.Value, utf8Json, kv.Key, "broadcast", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Broadcast skip session {SessionId} (send failed)", kv.Key);
            }
        }
    }

    private async Task SendUtf8JsonLockedAsync(SessionEntry entry, string utf8Json, string sessionId, string traceKind, CancellationToken cancellationToken)
    {
        if (entry.WebSocket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(utf8Json);
        await SendBytesLockedAsync(entry, bytes, sessionId, traceKind, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendBytesLockedAsync(SessionEntry entry, byte[] bytes, string sessionId, string traceKind, CancellationToken cancellationToken)
    {
        await entry.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.WebSocket.State != WebSocketState.Open) return;
            await entry.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSocket send failed session={SessionId} kind={Kind}", sessionId, traceKind);
        }
        finally
        {
            try { entry.SendLock.Release(); }
            catch (ObjectDisposedException) { /* connection torn down */ }
        }
    }

    private sealed record SessionEntry(
        WebSocket WebSocket,
        string? ClientType,
        string AgentProfileId,
        string AgentDisplayName,
        string? PageContextTitle,
        SemaphoreSlim SendLock);
}
