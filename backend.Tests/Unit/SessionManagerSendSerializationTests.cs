using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Unit;

/// <summary>Ensures per-session WebSocket sends are serialized (no concurrent SendAsync on the same socket).</summary>
public sealed class SessionManagerSendSerializationTests
{
    [Fact]
    public async Task Concurrent_SendToAsync_same_session_MaxOneSendAtATime()
    {
        using var ws = new CountingFakeWebSocket();
        var mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        mgr.Add("s1", ws, null, "default", "Test");

        var tasks = Enumerable.Range(0, 40)
            .Select(_ => mgr.SendToAsync("s1", """{"type":"ping"}""", CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(40, ws.SendCount);
        Assert.Equal(1, ws.MaxConcurrentSendDepth);
    }

    [Fact]
    public async Task SendToAsync_after_socket_closed_does_not_throw()
    {
        using var ws = new CountingFakeWebSocket();
        var mgr = new SessionManager(NullLogger<SessionManager>.Instance);
        mgr.Add("s1", ws, null, "default", "Test");
        ws.SetClosed();
        await mgr.SendToAsync("s1", """{"type":"x"}""", CancellationToken.None);
    }

    /// <summary>Minimal <see cref="WebSocket"/> that tracks concurrent <see cref="SendAsync"/> depth.</summary>
    private sealed class CountingFakeWebSocket : WebSocket
    {
        private volatile WebSocketState _state = WebSocketState.Open;
        private int _depth;
        private int _maxDepth;
        private int _sendCount;
        public int SendCount => _sendCount;

        public int MaxConcurrentSendDepth
        {
            get
            {
                lock (this)
                {
                    return _maxDepth;
                }
            }
        }

        public void SetClosed() => _state = WebSocketState.Closed;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var d = Interlocked.Increment(ref _depth);
            lock (this)
            {
                if (d > _maxDepth)
                    _maxDepth = d;
            }

            try
            {
                Interlocked.Increment(ref _sendCount);
                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _depth);
            }
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromException<WebSocketReceiveResult>(new InvalidOperationException("test double"));

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            GC.SuppressFinalize(this);
        }
    }
}
