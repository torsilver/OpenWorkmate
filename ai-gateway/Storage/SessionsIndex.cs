using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OpenWorkmate.AI.Gateway;

namespace OpenWorkmate.AI.Gateway.Storage;

public sealed class SessionsIndex
{
    private readonly IOptionsMonitor<AiGatewayOptions> _opt;
    private readonly object _lock = new();
    private SessionsIndexDocument _doc = new();
    private int _dirty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SessionsIndex(IOptionsMonitor<AiGatewayOptions> opt) => _opt = opt;

    private string IndexPath => Path.Combine(Path.GetFullPath(_opt.CurrentValue.DataRoot), "index", "sessions.idx.json");

    public void LoadOrRebuild()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(IndexPath))
                {
                    var t = File.ReadAllText(IndexPath);
                    var d = JsonSerializer.Deserialize<SessionsIndexDocument>(t, JsonRead);
                    if (d?.Sessions != null)
                    {
                        _doc = d;
                        return;
                    }
                }
            }
            catch
            {
                /* rebuild */
            }

            RebuildFromDiskUnlocked();
        }
    }

    private void RebuildFromDiskUnlocked()
    {
        var sessionsDir = Path.Combine(Path.GetFullPath(_opt.CurrentValue.DataRoot), "sessions");
        var doc = new SessionsIndexDocument { SchemaVersion = 1, Sessions = new Dictionary<string, SessionIndexEntry>() };
        if (!Directory.Exists(sessionsDir))
        {
            _doc = doc;
            FlushUnlocked();
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var sid = name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? name[..^".jsonl".Length]
                : name;
            if (string.IsNullOrWhiteSpace(sid)) continue;
            var fi = new FileInfo(file);
            if (!doc.Sessions.TryGetValue(sid, out var e))
            {
                e = new SessionIndexEntry
                {
                    FirstAt = fi.LastWriteTimeUtc,
                    LastAt = fi.LastWriteTimeUtc,
                    SizeBytes = 0,
                    Shards = new List<string>()
                };
                doc.Sessions[sid] = e;
            }
            e.Shards ??= new List<string>();
            if (!e.Shards.Contains(name)) e.Shards.Add(name);
            e.SizeBytes += fi.Length;
            e.LastAt = fi.LastWriteTimeUtc > e.LastAt ? fi.LastWriteTimeUtc : e.LastAt;
            e.FirstAt = fi.LastWriteTimeUtc < e.FirstAt ? fi.LastWriteTimeUtc : e.FirstAt;
        }

        _doc = doc;
        FlushUnlocked();
    }

    public void TouchSession(string sessionId, string shardFileName, long deltaBytes, int traceDelta = 0)
    {
        lock (_lock)
        {
            _doc.Sessions ??= new Dictionary<string, SessionIndexEntry>();
            if (!_doc.Sessions.TryGetValue(sessionId, out var e))
            {
                e = new SessionIndexEntry
                {
                    FirstAt = DateTime.UtcNow,
                    LastAt = DateTime.UtcNow,
                    TraceCount = 0,
                    SizeBytes = 0,
                    Shards = new List<string>()
                };
                _doc.Sessions[sessionId] = e;
            }
            e.Shards ??= new List<string>();
            if (!e.Shards.Contains(shardFileName)) e.Shards.Add(shardFileName);
            e.LastAt = DateTime.UtcNow;
            e.SizeBytes = Math.Max(0, e.SizeBytes + deltaBytes);
            e.TraceCount = Math.Max(0, e.TraceCount + traceDelta);
            _dirty++;
            if (_dirty >= 10)
            {
                FlushUnlocked();
                _dirty = 0;
            }
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            FlushUnlocked();
            _dirty = 0;
        }
    }

    private void FlushUnlocked()
    {
        var path = IndexPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_doc, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    public IReadOnlyDictionary<string, SessionIndexEntry> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, SessionIndexEntry>(_doc.Sessions ?? new Dictionary<string, SessionIndexEntry>());
        }
    }

    public void RemoveSession(string sessionId)
    {
        lock (_lock)
        {
            _doc.Sessions?.Remove(sessionId);
            FlushUnlocked();
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _doc = new SessionsIndexDocument { SchemaVersion = 1, Sessions = new Dictionary<string, SessionIndexEntry>() };
            FlushUnlocked();
        }
    }
}

public sealed class SessionsIndexDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("sessions")]
    public Dictionary<string, SessionIndexEntry> Sessions { get; set; } = new();
}

public sealed class SessionIndexEntry
{
    [JsonPropertyName("firstAt")]
    public DateTime FirstAt { get; set; }

    [JsonPropertyName("lastAt")]
    public DateTime LastAt { get; set; }

    [JsonPropertyName("traceCount")]
    public int TraceCount { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("shards")]
    public List<string> Shards { get; set; } = new();
}
