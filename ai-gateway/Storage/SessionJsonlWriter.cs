using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using Taskly.AI.Gateway;

namespace Taskly.AI.Gateway.Storage;

public sealed class SessionJsonlWriter : IDisposable
{
    private readonly IOptionsMonitor<AiGatewayOptions> _opt;
    private readonly SessionsIndex _index;
    private readonly ConcurrentDictionary<string, SessionWriterState> _sessions = new();

    public SessionJsonlWriter(IOptionsMonitor<AiGatewayOptions> opt, SessionsIndex index)
    {
        _opt = opt;
        _index = index;
    }

    private string SessionsDir => Path.Combine(Path.GetFullPath(_opt.CurrentValue.DataRoot), "sessions");

    public void AppendLine(string sessionId, string jsonLineUtf8)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = "unknown-session";
        sessionId = SanitizeSessionId(sessionId);
        Directory.CreateDirectory(SessionsDir);
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionWriterState());
        var sem = state.Lock;
        sem.Wait();
        try
        {
            var maxShard = Math.Max(1024 * 1024, _opt.CurrentValue.SessionJsonlShardMaxBytes);
            if (state.Stream == null || state.CurrentPath == null || state.BytesInShard > maxShard)
            {
                state.Stream?.Dispose();
                state.ShardIndex++;
                var fname = state.ShardIndex == 0 ? $"{sessionId}.jsonl" : $"{sessionId}.{state.ShardIndex:D4}.jsonl";
                var path = Path.Combine(SessionsDir, fname);
                state.Stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                state.CurrentPath = fname;
                state.BytesInShard = 0;
            }

            var bytes = Encoding.UTF8.GetBytes(jsonLineUtf8 + "\n");
            state.Stream.Write(bytes);
            state.Stream.Flush(true);
            state.BytesInShard += bytes.Length;
            _index.TouchSession(sessionId, state.CurrentPath!, bytes.Length, traceDelta: 0);
        }
        finally
        {
            sem.Release();
        }
    }

    private static string SanitizeSessionId(string id)
    {
        var t = (id ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            t = t.Replace(c, '_');
        if (t.Length > 120) t = t[..120];
        return string.IsNullOrEmpty(t) ? "unknown" : t;
    }

    public void Dispose()
    {
        foreach (var kv in _sessions)
        {
            kv.Value.Lock.Wait();
            try
            {
                kv.Value.Stream?.Dispose();
                kv.Value.Stream = null;
            }
            finally
            {
                kv.Value.Lock.Release();
            }
        }
    }

    private sealed class SessionWriterState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public FileStream? Stream;
        public string? CurrentPath;
        public int ShardIndex = -1;
        public long BytesInShard;
    }
}
