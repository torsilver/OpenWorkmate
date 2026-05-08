using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Services.ToolInvocation;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// 调试统计：主会话工具阶段（含动态工具 bootstrap）与各工具调用成功/失败次数。默认持久化到本机用户目录。
/// </summary>
public sealed class AgentDebugStatsService : IDisposable
{
    public const int PersistedFormatVersion = 5;
    private readonly object _gate = new();
    private readonly object _persistScheduleLock = new();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly ILogger<AgentDebugStatsService> _logger;
    private readonly string _persistencePath;
    private CancellationTokenSource? _persistDebouncerCts;
    private CancellationTokenRegistration _stoppingRegistration;
    private DateTimeOffset? _accumulatedSinceUtc;

    private long _toolingPhaseTotal;
    private long _toolSelectionException;
    private long _dynamicToolingBootstrapCount;
    private long _toolFailureBindingCount;
    private long _toolFailureMcpCount;
    private long _toolFailureBusinessCount;
    private readonly Dictionary<string, (long Success, long Fail)> _toolInvocations = new(StringComparer.Ordinal);

    /// <summary>生产环境：默认路径为 %LocalAppData%\OpenWorkmate\agent-debug-stats.json。</summary>
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
        return Path.Combine(appData, "OpenWorkmate", "agent-debug-stats.json");
    }

    /// <summary>测试或关机前强制同步落盘（跳过防抖）。</summary>
    internal void FlushPersistenceForTests() => PersistToDiskSync();

    public void IncrementToolSelectionTotal()
    {
        lock (_gate) { _toolingPhaseTotal++; }
        SchedulePersist();
    }

    public void RecordToolSelectionException()
    {
        lock (_gate) { _toolSelectionException++; }
        SchedulePersist();
    }

    public void RecordDynamicToolingBootstrap()
    {
        lock (_gate) { _dynamicToolingBootstrapCount++; }
        SchedulePersist();
    }

    /// <summary>与下发给前端的 success 一致（管道 success + <see cref="ToolSemanticFailureMarkers"/> 前缀语义失败）。</summary>
    public void RecordToolInvocation(string pluginName, string functionName, bool success)
        => RecordToolInvocation(pluginName, functionName, success, null);

    /// <param name="failureKindOnFail">仅在最终判定为失败时用于聚合阶段；成功时忽略。</param>
    public void RecordToolInvocation(string pluginName, string functionName, bool success, ToolInvocationFailureKind? failureKindOnFail)
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

            if (!success && failureKindOnFail.HasValue)
            {
                switch (failureKindOnFail.Value)
                {
                    case ToolInvocationFailureKind.Binding:
                        _toolFailureBindingCount++;
                        break;
                    case ToolInvocationFailureKind.Mcp:
                        _toolFailureMcpCount++;
                        break;
                    default:
                        _toolFailureBusinessCount++;
                        break;
                }
            }
        }
        SchedulePersist();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _toolingPhaseTotal = 0;
            _toolSelectionException = 0;
            _dynamicToolingBootstrapCount = 0;
            _toolFailureBindingCount = 0;
            _toolFailureMcpCount = 0;
            _toolFailureBusinessCount = 0;
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
            var total = _toolingPhaseTotal;
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
                    DynamicToolingBootstrapCount = _dynamicToolingBootstrapCount,
                    DynamicBootstrapRateAmongToolingPhases = total > 0 ? (double)_dynamicToolingBootstrapCount / total : null,
                    ToolFailureBindingCount = _toolFailureBindingCount,
                    ToolFailureMcpCount = _toolFailureMcpCount,
                    ToolFailureBusinessCount = _toolFailureBusinessCount,
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
        if (_toolingPhaseTotal != 0 || _toolSelectionException != 0 || _dynamicToolingBootstrapCount != 0)
            return true;
        if (_toolFailureBindingCount != 0 || _toolFailureMcpCount != 0 || _toolFailureBusinessCount != 0)
            return true;
        return false;
    }

    private AgentDebugStatsPersistedModel CapturePersistedModel() =>
        new()
        {
            Version = PersistedFormatVersion,
            AccumulatedSinceUtc = _accumulatedSinceUtc,
            ToolingPhaseTotal = _toolingPhaseTotal,
            SelectionExceptionFallbackCount = _toolSelectionException,
            DynamicToolingBootstrapCount = _dynamicToolingBootstrapCount,
            ToolFailureBindingCount = _toolFailureBindingCount,
            ToolFailureMcpCount = _toolFailureMcpCount,
            ToolFailureBusinessCount = _toolFailureBusinessCount,
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
            _toolingPhaseTotal = m.ToolingPhaseTotal;
            _toolSelectionException = m.SelectionExceptionFallbackCount;
            _dynamicToolingBootstrapCount = m.DynamicToolingBootstrapCount;
            _toolFailureBindingCount = m.ToolFailureBindingCount;
            _toolFailureMcpCount = m.ToolFailureMcpCount;
            _toolFailureBusinessCount = m.ToolFailureBusinessCount;
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
    public long ToolingPhaseTotal { get; set; }
    public long SelectionExceptionFallbackCount { get; set; }
    public long DynamicToolingBootstrapCount { get; set; }
    public long ToolFailureBindingCount { get; set; }
    public long ToolFailureMcpCount { get; set; }
    public long ToolFailureBusinessCount { get; set; }
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
    public long DynamicToolingBootstrapCount { get; init; }
    /// <summary>动态工具首轮 bootstrap 次数 / 工具阶段总次数；分母为 0 时为 null。</summary>
    public double? DynamicBootstrapRateAmongToolingPhases { get; init; }

    /// <summary>工具调用失败且判定为参数绑定阶段的累计次数。</summary>
    public long ToolFailureBindingCount { get; init; }

    /// <summary>工具调用失败且判定为 MCP 阶段的累计次数。</summary>
    public long ToolFailureMcpCount { get; init; }

    /// <summary>工具调用失败且判定为业务/其它阶段的累计次数。</summary>
    public long ToolFailureBusinessCount { get; init; }
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
