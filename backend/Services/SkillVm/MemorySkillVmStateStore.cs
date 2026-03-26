using System.Collections.Concurrent;
using System.Text.Json;

namespace OfficeCopilot.Server.Services.SkillVm;

public sealed class MemorySkillVmStateStore : ISkillVmStateStore
{
    private readonly ConcurrentDictionary<string, SkillVmState> _memory = new(StringComparer.Ordinal);
    private readonly ConfigService _config;
    private readonly ILogger<MemorySkillVmStateStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public MemorySkillVmStateStore(ConfigService config, ILogger<MemorySkillVmStateStore> logger)
    {
        _config = config;
        _logger = logger;
    }

    public SkillVmState GetOrCreate(string sessionId, string activeSkillId, string initialSegmentId)
    {
        return _memory.AddOrUpdate(
            sessionId,
            _ => new SkillVmState
            {
                SessionId = sessionId,
                ActiveSkillId = activeSkillId,
                CurrentSegmentId = initialSegmentId
            },
            (_, existing) =>
            {
                if (!string.Equals(existing.ActiveSkillId, activeSkillId, StringComparison.OrdinalIgnoreCase))
                {
                    existing.ActiveSkillId = activeSkillId;
                    existing.CurrentSegmentId = initialSegmentId;
                    existing.Stack.Clear();
                    existing.CompletedSegmentIds.Clear();
                    existing.Finished = false;
                    existing.Paused = false;
                    existing.Variables.Clear();
                }
                return existing;
            });
    }

    public bool TryGet(string sessionId, out SkillVmState? state)
    {
        return _memory.TryGetValue(sessionId, out state);
    }

    public void Update(string sessionId, SkillVmState state)
    {
        state.SessionId = sessionId;
        _memory[sessionId] = state;
    }

    public void Clear(string sessionId) => _memory.TryRemove(sessionId, out _);

    public void Persist(string sessionId, SkillVmState state)
    {
        var dir = GetDataDirectory();
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SanitizeFileName(sessionId) + ".json");
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkillVm persist failed for session {SessionId}", sessionId);
        }
    }

    public bool TryLoadPersisted(string sessionId, out SkillVmState? state)
    {
        state = null;
        var dir = GetDataDirectory();
        if (string.IsNullOrEmpty(dir)) return false;
        var path = Path.Combine(dir, SanitizeFileName(sessionId) + ".json");
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            state = JsonSerializer.Deserialize<SkillVmState>(json, JsonOptions);
            if (state != null)
            {
                state.SessionId = sessionId;
                _memory[sessionId] = state;
            }
            return state != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkillVm load failed for session {SessionId}", sessionId);
            return false;
        }
    }

    private string? GetDataDirectory()
    {
        var cfg = _config.Current.SkillVm?.DataDirectory?.Trim();
        if (!string.IsNullOrEmpty(cfg))
        {
            cfg = Environment.ExpandEnvironmentVariables(cfg);
            if (!Path.IsPathRooted(cfg))
                cfg = Path.Combine(AppContext.BaseDirectory, cfg);
            return cfg;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "OfficeCopilot", "SkillVm");
    }

    private static string SanitizeFileName(string sessionId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = sessionId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
