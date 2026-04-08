using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OfficeCopilot.Server;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 调试统计：两阶段工具选择与各工具调用成功/失败次数。默认持久化到本机用户目录，跨进程重启保留；Reset 与清空计数会删除持久化文件。
/// </summary>
public sealed class AgentDebugStatsService : IDisposable
{
    public const int PersistedFormatVersion = 2;
    private readonly object _gate = new();
    private readonly object _persistScheduleLock = new();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly ILogger<AgentDebugStatsService> _logger;
    private readonly string _persistencePath;
    private CancellationTokenSource? _persistDebouncerCts;
    private CancellationTokenRegistration _stoppingRegistration;
    private DateTimeOffset? _accumulatedSinceUtc;

    private long _toolSelectionTotal;
    private long _toolSelectionException;
    private long _twoStageUsed;
    private readonly Dictionary<string, (long Success, long Fail)> _toolInvocations = new(StringComparer.Ordinal);

    /// <summary>生产环境：默认路径为 %LocalAppData%\OfficeCopilot\agent-debug-stats.json。</summary>
    public AgentDebugStatsService(ILogger<AgentDebugStatsService> logger, IHostApplicationLifetime applicationLifetime)
        : this(logger, GetDefaultPersistencePath(), applicationLifetime) { }

    /// <summary>单元测试：指定持久化文件路径；<paramref name="applicationLifetime"/> 可为 null（不注册停止刷盘）。</summary>
    internal AgentDebugStatsService(ILogger<AgentDebugStatsService> logger, string persistencePath, IHostApplicationLifetime? applicationLifetime = null)
    {
        _logger = logger;
        _persistencePath = persistencePath ?? GetDefaultPersistencePath();
        LoadFromDisk();
        if (applicationLifetime != null)
            _stoppingRegistration = applicationLifetime.ApplicationStopping.Register(PersistToDiskSync);
    }

    public void Dispose()
    {
        try
        {
            PersistToDiskSync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentDebugStats: final persist on dispose failed.");
        }
        _stoppingRegistration.Dispose();
        lock (_persistScheduleLock)
        {
            _persistDebouncerCts?.Cancel();
            _persistDebouncerCts?.Dispose();
            _persistDebouncerCts = null;
        }
    }

    public static string GetDefaultPersistencePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "OfficeCopilot", "agent-debug-stats.json");
    }

    /// <summary>测试或关机前强制同步落盘（跳过防抖）。</summary>
    internal void FlushPersistenceForTests() => PersistToDiskSync();

    public void IncrementToolSelectionTotal()
    {
        lock (_gate) { _toolSelectionTotal++; }
        SchedulePersist();
    }

    public void RecordToolSelectionException()
    {
        lock (_gate) { _toolSelectionException++; }
        SchedulePersist();
    }

    public void RecordTwoStageUsed()
    {
        lock (_gate) { _twoStageUsed++; }
        SchedulePersist();
    }

    /// <summary>与 <see cref="ToolStatusFilter"/> 下发给前端的 success 一致（含返回串判失败）。</summary>
    public void RecordToolInvocation(string pluginName, string functionName, bool success)
    {
        var key = $"{pluginName}.{functionName}";
        lock (_gate)
        {
            if (!_toolInvocations.TryGetValue(key, out var t))
                t = (0, 0);
            if (success)
                t.Success++;
            else
                t.Fail++;
            _toolInvocations[key] = t;
        }
        SchedulePersist();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _toolSelectionTotal = 0;
            _toolSelectionException = 0;
            _twoStageUsed = 0;
            _toolInvocations.Clear();
            _accumulatedSinceUtc = null;
        }
        lock (_persistScheduleLock)
        {
            _persistDebouncerCts?.Cancel();
            _persistDebouncerCts?.Dispose();
            _persistDebouncerCts = null;
        }
        TryDeletePersistenceFile();
    }

    public AgentDebugStatsResponse GetSnapshot()
    {
        lock (_gate)
        {
            var total = _toolSelectionTotal;
            var list = _toolInvocations
                .Select(kv => ToolInvocationDebugStatDto.From(kv.Key, kv.Value.Success, kv.Value.Fail))
                .OrderByDescending(x => x.TotalCalls)
                .ThenBy(x => x.ToolId, StringComparer.Ordinal)
                .ToList();

            return new AgentDebugStatsResponse
            {
                ServerStartedUtc = _startedUtc,
                StatsAccumulatedSinceUtc = _accumulatedSinceUtc,
                ToolSelection = new ToolSelectionDebugStatsDto
                {
                    TotalNonPlanSelections = total,
                    SelectionExceptionFallbackCount = _toolSelectionException,
                    TwoStageInvocationsCount = _twoStageUsed,
                    TwoStageRateAmongSelections = total > 0 ? (double)_twoStageUsed / total : null
                },
                ToolInvocations = list
            };
        }
    }

    private void SchedulePersist()
    {
        lock (_persistScheduleLock)
        {
            _persistDebouncerCts?.Cancel();
            _persistDebouncerCts?.Dispose();
            _persistDebouncerCts = new CancellationTokenSource();
            var ct = _persistDebouncerCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;
                    PersistToDiskSync();
                }
                catch (OperationCanceledException) { /* debounce replaced */ }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AgentDebugStats: debounced persist task failed.");
                }
            });
        }
    }

    private void PersistToDiskSync()
    {
        AgentDebugStatsPersistedModel model;
        lock (_gate)
        {
            if (!HasNontrivialState())
            {
                _accumulatedSinceUtc = null;
                TryDeletePersistenceFile();
                return;
            }

            if (!_accumulatedSinceUtc.HasValue)
                _accumulatedSinceUtc = DateTimeOffset.UtcNow;

            model = CapturePersistedModel();
        }

        TryWriteFile(model);
    }

    private void TryDeletePersistenceFile()
    {
        try
        {
            if (File.Exists(_persistencePath))
                File.Delete(_persistencePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentDebugStats: failed to delete persistence file {Path}.", _persistencePath);
        }
    }

    private bool HasNontrivialState()
    {
        if (_toolInvocations.Count > 0)
            return true;
        if (_toolSelectionTotal != 0 || _toolSelectionException != 0 || _twoStageUsed != 0)
            return true;
        return false;
    }

    private AgentDebugStatsPersistedModel CapturePersistedModel() =>
        new()
        {
            Version = PersistedFormatVersion,
            AccumulatedSinceUtc = _accumulatedSinceUtc,
            ToolSelectionTotal = _toolSelectionTotal,
            SelectionExceptionFallbackCount = _toolSelectionException,
            TwoStageInvocationsCount = _twoStageUsed,
            ToolInvocations = _toolInvocations
                .Select(kv => new PersistedToolInvocationEntry { ToolId = kv.Key, SuccessCount = kv.Value.Success, FailCount = kv.Value.Fail })
                .ToList()
        };

    private void TryWriteFile(AgentDebugStatsPersistedModel model)
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistencePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(model, Utf8JsonFileOptions.Indented);
            var tmp = _persistencePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _persistencePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentDebugStats: failed to write persistence file {Path}.", _persistencePath);
        }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_persistencePath))
            return;
        try
        {
            var json = File.ReadAllText(_persistencePath);
            var model = JsonSerializer.Deserialize<AgentDebugStatsPersistedModel>(json, Utf8JsonFileOptions.Indented);
            if (model == null || model.Version != PersistedFormatVersion)
            {
                _logger.LogWarning("AgentDebugStats: persistence file version mismatch or empty, starting fresh. Path={Path}", _persistencePath);
                TryRenameCorruptFile();
                return;
            }
            ApplyPersistedModel(model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentDebugStats: failed to load persistence file {Path}, starting fresh.", _persistencePath);
            TryRenameCorruptFile();
        }
    }

    private void TryRenameCorruptFile()
    {
        try
        {
            if (!File.Exists(_persistencePath)) return;
            var bak = _persistencePath + ".bad." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(_persistencePath, bak);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentDebugStats: could not rename corrupt persistence file.");
        }
    }

    private void ApplyPersistedModel(AgentDebugStatsPersistedModel m)
    {
        lock (_gate)
        {
            _accumulatedSinceUtc = m.AccumulatedSinceUtc;
            _toolSelectionTotal = m.ToolSelectionTotal;
            _toolSelectionException = m.SelectionExceptionFallbackCount;
            _twoStageUsed = m.TwoStageInvocationsCount;
            _toolInvocations.Clear();
            foreach (var e in m.ToolInvocations ?? Enumerable.Empty<PersistedToolInvocationEntry>())
            {
                if (string.IsNullOrWhiteSpace(e.ToolId)) continue;
                _toolInvocations[e.ToolId] = (e.SuccessCount, e.FailCount);
            }
        }
    }
}

internal sealed class AgentDebugStatsPersistedModel
{
    public int Version { get; set; } = AgentDebugStatsService.PersistedFormatVersion;
    public DateTimeOffset? AccumulatedSinceUtc { get; set; }
    public long ToolSelectionTotal { get; set; }
    public long SelectionExceptionFallbackCount { get; set; }
    public long TwoStageInvocationsCount { get; set; }
    public List<PersistedToolInvocationEntry> ToolInvocations { get; set; } = new();
}

internal sealed class PersistedToolInvocationEntry
{
    public string ToolId { get; set; } = "";
    public long SuccessCount { get; set; }
    public long FailCount { get; set; }
}

public sealed class AgentDebugStatsResponse
{
    public DateTimeOffset ServerStartedUtc { get; init; }
    /// <summary>持久化累计统计的起始时间（UTC）；无历史时为 null。当前进程启动时间见 <see cref="ServerStartedUtc"/>。</summary>
    public DateTimeOffset? StatsAccumulatedSinceUtc { get; init; }
    public ToolSelectionDebugStatsDto ToolSelection { get; init; } = new();
    public List<ToolInvocationDebugStatDto> ToolInvocations { get; init; } = new();
}

public sealed class ToolSelectionDebugStatsDto
{
    public long TotalNonPlanSelections { get; init; }
    public long SelectionExceptionFallbackCount { get; init; }
    public long TwoStageInvocationsCount { get; init; }
    /// <summary>两阶段调用次数 / 非计划工具选择总次数；分母为 0 时为 null。</summary>
    public double? TwoStageRateAmongSelections { get; init; }
}

public sealed class ToolInvocationDebugStatDto
{
    public string ToolId { get; init; } = "";
    public long SuccessCount { get; init; }
    public long FailCount { get; init; }
    public long TotalCalls { get; init; }
    public double? SuccessRate { get; init; }

    public static ToolInvocationDebugStatDto From(string toolId, long success, long fail)
    {
        var total = success + fail;
        return new ToolInvocationDebugStatDto
        {
            ToolId = toolId,
            SuccessCount = success,
            FailCount = fail,
            TotalCalls = total,
            SuccessRate = total > 0 ? (double)success / total : null
        };
    }
}

public sealed class DebugStatsResetResponse
{
    public bool Ok { get; init; } = true;
}
