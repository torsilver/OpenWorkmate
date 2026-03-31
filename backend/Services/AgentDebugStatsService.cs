using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server.Services;

/// <summary>单次向量检索完成后的遥测（进程内累计用）。</summary>
public readonly record struct VectorSearchTelemetry(
    string? ClientType,
    double MaxScore,
    double? SecondBestScore,
    int DistinctHitCount,
    bool GoodEnough,
    bool VectorFirstPathChosen);

/// <summary>
/// 调试统计：工具向量选择路径、各工具调用成功/失败次数。默认持久化到本机用户目录，跨进程重启保留；Reset 与清空计数会删除持久化文件。
/// </summary>
public sealed class AgentDebugStatsService : IDisposable
{
    public const int PersistedFormatVersion = 1;
    private const int HistogramBucketCount = 5;
    private static readonly JsonSerializerOptions JsonFileOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

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
    private long _vectorSkippedNoEmbedding;
    private long _vectorSkippedNonPersistent;
    private long _vectorSearchRuns;
    private long _vectorGoodEnoughFlagTrue;
    private long _vectorFirstPathChosen;
    private long _vectorSearchRanButFallbackToTwoStage;
    private long _twoStageUsed;
    /// <summary>本轮已执行向量检索且随后调用了两阶段子类选择的次数（向量未收窄后的「第二轮」）。</summary>
    private long _vectorThenTwoStageAttempts;
    /// <summary>上述路径下两阶段返回 <see cref="ToolSelectionOutcome.SelectedPairs"/> 为 null、即仍用全量工具的次数。</summary>
    private long _vectorThenTwoStageFullTools;
    private long _vectorGoodEnoughTrueButEmptyResults;
    private readonly long[] _maxScoreHistogram = new long[HistogramBucketCount];
    private double _maxScoreSum;
    private long _distinctHitCountSum;
    private double _top1MinusTop2Sum;
    private long _top1MinusTop2SampleCount;
    private readonly Dictionary<string, (long Runs, double MaxScoreSum)> _vectorByClient = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>与直方图桶顺序一致，供 API/前端展示标签。</summary>
    public static IReadOnlyList<string> MaxScoreHistogramLabels { get; } = new[]
    {
        "[0.0, 0.5)",
        "[0.5, 0.6)",
        "[0.6, 0.7)",
        "[0.7, 0.8)",
        "[0.8, 1.0]"
    };

    public static int MaxScoreToHistogramBucket(double maxScore)
    {
        if (maxScore < 0.5) return 0;
        if (maxScore < 0.6) return 1;
        if (maxScore < 0.7) return 2;
        if (maxScore < 0.8) return 3;
        return 4;
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

    public void RecordVectorSkippedNoEmbedding()
    {
        lock (_gate) { _vectorSkippedNoEmbedding++; }
        SchedulePersist();
    }

    public void RecordVectorSkippedNonPersistent()
    {
        lock (_gate) { _vectorSkippedNonPersistent++; }
        SchedulePersist();
    }

    /// <param name="telemetry">一次向量检索的分数与路径信息。</param>
    public void RecordVectorSearchCompleted(in VectorSearchTelemetry telemetry)
    {
        lock (_gate)
        {
            _vectorSearchRuns++;
            if (telemetry.GoodEnough)
                _vectorGoodEnoughFlagTrue++;
            if (telemetry.GoodEnough && telemetry.DistinctHitCount == 0)
                _vectorGoodEnoughTrueButEmptyResults++;
            if (telemetry.VectorFirstPathChosen)
                _vectorFirstPathChosen++;
            else
                _vectorSearchRanButFallbackToTwoStage++;

            var b = MaxScoreToHistogramBucket(telemetry.MaxScore);
            _maxScoreHistogram[b]++;

            _maxScoreSum += telemetry.MaxScore;
            _distinctHitCountSum += telemetry.DistinctHitCount;

            if (telemetry.DistinctHitCount >= 2 && telemetry.SecondBestScore.HasValue)
            {
                _top1MinusTop2Sum += telemetry.MaxScore - telemetry.SecondBestScore.Value;
                _top1MinusTop2SampleCount++;
            }

            var ctKey = string.IsNullOrWhiteSpace(telemetry.ClientType) ? "chrome" : telemetry.ClientType.Trim();
            if (!_vectorByClient.TryGetValue(ctKey, out var byC))
                byC = (Runs: 0L, MaxScoreSum: 0.0);
            _vectorByClient[ctKey] = (byC.Runs + 1, byC.MaxScoreSum + telemetry.MaxScore);
        }
        SchedulePersist();
    }

    public void RecordTwoStageUsed()
    {
        lock (_gate) { _twoStageUsed++; }
        SchedulePersist();
    }

    /// <summary>
    /// 在「已执行向量检索、未走向量优先」且随后完成两阶段选择后调用。
    /// <paramref name="endedWithFullTools"/> 为 true 表示两阶段结果为全量工具（SelectedPairs 为空）。
    /// </summary>
    public void RecordVectorThenTwoStageOutcome(bool endedWithFullTools)
    {
        lock (_gate)
        {
            _vectorThenTwoStageAttempts++;
            if (endedWithFullTools)
                _vectorThenTwoStageFullTools++;
        }
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
            _vectorSkippedNoEmbedding = 0;
            _vectorSkippedNonPersistent = 0;
            _vectorSearchRuns = 0;
            _vectorGoodEnoughFlagTrue = 0;
            _vectorFirstPathChosen = 0;
            _vectorSearchRanButFallbackToTwoStage = 0;
            _twoStageUsed = 0;
            _vectorThenTwoStageAttempts = 0;
            _vectorThenTwoStageFullTools = 0;
            _vectorGoodEnoughTrueButEmptyResults = 0;
            Array.Clear(_maxScoreHistogram);
            _maxScoreSum = 0;
            _distinctHitCountSum = 0;
            _top1MinusTop2Sum = 0;
            _top1MinusTop2SampleCount = 0;
            _vectorByClient.Clear();
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
            var vecRuns = _vectorSearchRuns;
            var vecFirst = _vectorFirstPathChosen;
            var list = _toolInvocations
                .Select(kv => ToolInvocationDebugStatDto.From(kv.Key, kv.Value.Success, kv.Value.Fail))
                .OrderByDescending(x => x.TotalCalls)
                .ThenBy(x => x.ToolId, StringComparer.Ordinal)
                .ToList();

            var labels = MaxScoreHistogramLabels;
            var histogram = new List<VectorScoreHistogramBucketDto>(HistogramBucketCount);
            for (var i = 0; i < HistogramBucketCount; i++)
            {
                histogram.Add(new VectorScoreHistogramBucketDto
                {
                    Label = labels[i],
                    Count = _maxScoreHistogram[i]
                });
            }

            var byClient = _vectorByClient
                .Select(kv => new VectorSearchClientTypeStatDto
                {
                    ClientType = kv.Key,
                    VectorSearchRunCount = kv.Value.Runs,
                    AverageMaxScore = kv.Value.Runs > 0 ? kv.Value.MaxScoreSum / kv.Value.Runs : null
                })
                .OrderByDescending(x => x.VectorSearchRunCount)
                .ThenBy(x => x.ClientType, StringComparer.Ordinal)
                .ToList();

            var goodEnoughFalse = vecRuns - _vectorGoodEnoughFlagTrue;
            var vtsAttempts = _vectorThenTwoStageAttempts;
            var vtsFull = _vectorThenTwoStageFullTools;

            return new AgentDebugStatsResponse
            {
                ServerStartedUtc = _startedUtc,
                StatsAccumulatedSinceUtc = _accumulatedSinceUtc,
                ToolSelection = new ToolSelectionDebugStatsDto
                {
                    TotalNonPlanSelections = total,
                    SelectionExceptionFallbackCount = _toolSelectionException,
                    VectorSkippedNoEmbeddingCount = _vectorSkippedNoEmbedding,
                    VectorSkippedNonPersistentStoreCount = _vectorSkippedNonPersistent,
                    VectorSearchRunCount = vecRuns,
                    VectorGoodEnoughTrueCount = _vectorGoodEnoughFlagTrue,
                    VectorGoodEnoughFalseCount = goodEnoughFalse,
                    VectorGoodEnoughTrueButEmptyResultsCount = _vectorGoodEnoughTrueButEmptyResults,
                    VectorFirstPathChosenCount = vecFirst,
                    VectorSearchButTwoStageCount = _vectorSearchRanButFallbackToTwoStage,
                    TwoStageInvocationsCount = _twoStageUsed,
                    VectorThenTwoStageCount = vtsAttempts,
                    VectorThenTwoStageFullToolsCount = vtsFull,
                    VectorThenTwoStageFullToolsRate = vtsAttempts > 0 ? (double)vtsFull / vtsAttempts : null,
                    VectorFirstPathRateAmongVectorSearches = vecRuns > 0 ? (double)vecFirst / vecRuns : null,
                    VectorFirstPathRateAmongSelections = total > 0 ? (double)vecFirst / total : null,
                    TwoStageRateAmongSelections = total > 0 ? (double)_twoStageUsed / total : null,
                    VectorGoodEnoughFalseRateAmongVectorSearches = vecRuns > 0 ? (double)goodEnoughFalse / vecRuns : null,
                    AverageMaxScoreAmongVectorSearches = vecRuns > 0 ? _maxScoreSum / vecRuns : null,
                    AverageDistinctHitCountAmongVectorSearches = vecRuns > 0 ? (double)_distinctHitCountSum / vecRuns : null,
                    AverageTop1MinusTop2AmongVectorSearches = _top1MinusTop2SampleCount > 0
                        ? _top1MinusTop2Sum / _top1MinusTop2SampleCount
                        : null,
                    Top1MinusTop2SampleCount = _top1MinusTop2SampleCount,
                    MaxScoreHistogram = histogram,
                    VectorSearchByClientType = byClient
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
        if (_toolInvocations.Count > 0 || _vectorByClient.Count > 0)
            return true;
        if (_toolSelectionTotal != 0 || _toolSelectionException != 0 || _vectorSkippedNoEmbedding != 0 || _vectorSkippedNonPersistent != 0)
            return true;
        if (_vectorSearchRuns != 0 || _vectorGoodEnoughFlagTrue != 0 || _vectorFirstPathChosen != 0
            || _vectorSearchRanButFallbackToTwoStage != 0 || _twoStageUsed != 0 || _vectorThenTwoStageAttempts != 0
            || _vectorThenTwoStageFullTools != 0 || _vectorGoodEnoughTrueButEmptyResults != 0)
            return true;
        if (_maxScoreSum != 0 || _distinctHitCountSum != 0 || _top1MinusTop2Sum != 0 || _top1MinusTop2SampleCount != 0)
            return true;
        for (var i = 0; i < _maxScoreHistogram.Length; i++)
        {
            if (_maxScoreHistogram[i] != 0)
                return true;
        }
        return false;
    }

    private AgentDebugStatsPersistedModel CapturePersistedModel()
    {
        var hist = new long[HistogramBucketCount];
        Array.Copy(_maxScoreHistogram, hist, HistogramBucketCount);
        return new AgentDebugStatsPersistedModel
        {
            Version = PersistedFormatVersion,
            AccumulatedSinceUtc = _accumulatedSinceUtc,
            ToolSelectionTotal = _toolSelectionTotal,
            SelectionExceptionFallbackCount = _toolSelectionException,
            VectorSkippedNoEmbeddingCount = _vectorSkippedNoEmbedding,
            VectorSkippedNonPersistentStoreCount = _vectorSkippedNonPersistent,
            VectorSearchRunCount = _vectorSearchRuns,
            VectorGoodEnoughTrueCount = _vectorGoodEnoughFlagTrue,
            VectorFirstPathChosenCount = _vectorFirstPathChosen,
            VectorSearchButTwoStageCount = _vectorSearchRanButFallbackToTwoStage,
            TwoStageInvocationsCount = _twoStageUsed,
            VectorThenTwoStageCount = _vectorThenTwoStageAttempts,
            VectorThenTwoStageFullToolsCount = _vectorThenTwoStageFullTools,
            VectorGoodEnoughTrueButEmptyResultsCount = _vectorGoodEnoughTrueButEmptyResults,
            MaxScoreHistogram = hist,
            MaxScoreSum = _maxScoreSum,
            DistinctHitCountSum = _distinctHitCountSum,
            Top1MinusTop2Sum = _top1MinusTop2Sum,
            Top1MinusTop2SampleCount = _top1MinusTop2SampleCount,
            VectorByClient = _vectorByClient
                .Select(kv => new PersistedVectorByClientEntry { ClientType = kv.Key, Runs = kv.Value.Runs, MaxScoreSum = kv.Value.MaxScoreSum })
                .ToList(),
            ToolInvocations = _toolInvocations
                .Select(kv => new PersistedToolInvocationEntry { ToolId = kv.Key, SuccessCount = kv.Value.Success, FailCount = kv.Value.Fail })
                .ToList()
        };
    }

    private void TryWriteFile(AgentDebugStatsPersistedModel model)
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistencePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(model, JsonFileOptions);
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
            var model = JsonSerializer.Deserialize<AgentDebugStatsPersistedModel>(json, JsonFileOptions);
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
            _vectorSkippedNoEmbedding = m.VectorSkippedNoEmbeddingCount;
            _vectorSkippedNonPersistent = m.VectorSkippedNonPersistentStoreCount;
            _vectorSearchRuns = m.VectorSearchRunCount;
            _vectorGoodEnoughFlagTrue = m.VectorGoodEnoughTrueCount;
            _vectorFirstPathChosen = m.VectorFirstPathChosenCount;
            _vectorSearchRanButFallbackToTwoStage = m.VectorSearchButTwoStageCount;
            _twoStageUsed = m.TwoStageInvocationsCount;
            _vectorThenTwoStageAttempts = m.VectorThenTwoStageCount;
            _vectorThenTwoStageFullTools = m.VectorThenTwoStageFullToolsCount;
            _vectorGoodEnoughTrueButEmptyResults = m.VectorGoodEnoughTrueButEmptyResultsCount;
            Array.Clear(_maxScoreHistogram);
            if (m.MaxScoreHistogram != null && m.MaxScoreHistogram.Length == HistogramBucketCount)
                Array.Copy(m.MaxScoreHistogram, _maxScoreHistogram, HistogramBucketCount);
            _maxScoreSum = m.MaxScoreSum;
            _distinctHitCountSum = m.DistinctHitCountSum;
            _top1MinusTop2Sum = m.Top1MinusTop2Sum;
            _top1MinusTop2SampleCount = m.Top1MinusTop2SampleCount;
            _vectorByClient.Clear();
            foreach (var e in m.VectorByClient ?? Enumerable.Empty<PersistedVectorByClientEntry>())
            {
                if (string.IsNullOrWhiteSpace(e.ClientType)) continue;
                _vectorByClient[e.ClientType.Trim()] = (e.Runs, e.MaxScoreSum);
            }
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
    public long VectorSkippedNoEmbeddingCount { get; set; }
    public long VectorSkippedNonPersistentStoreCount { get; set; }
    public long VectorSearchRunCount { get; set; }
    public long VectorGoodEnoughTrueCount { get; set; }
    public long VectorFirstPathChosenCount { get; set; }
    public long VectorSearchButTwoStageCount { get; set; }
    public long TwoStageInvocationsCount { get; set; }
    public long VectorThenTwoStageCount { get; set; }
    public long VectorThenTwoStageFullToolsCount { get; set; }
    public long VectorGoodEnoughTrueButEmptyResultsCount { get; set; }
    public long[] MaxScoreHistogram { get; set; } = new long[5];
    public double MaxScoreSum { get; set; }
    public long DistinctHitCountSum { get; set; }
    public double Top1MinusTop2Sum { get; set; }
    public long Top1MinusTop2SampleCount { get; set; }
    public List<PersistedVectorByClientEntry> VectorByClient { get; set; } = new();
    public List<PersistedToolInvocationEntry> ToolInvocations { get; set; } = new();
}

internal sealed class PersistedVectorByClientEntry
{
    public string ClientType { get; set; } = "";
    public long Runs { get; set; }
    public double MaxScoreSum { get; set; }
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
    /// <summary>当前服务端 ContextWindow 中的工具向量检索阈值快照（仅 debug 接口填充）。</summary>
    public ToolSearchConfigSnapshotDto? ToolSearchConfig { get; init; }
}

public sealed class ToolSearchConfigSnapshotDto
{
    public int ToolSearchTopK { get; init; }
    public double ToolSearchMinScore { get; init; }
    public int ToolSearchMinCount { get; init; }
}

public sealed class VectorScoreHistogramBucketDto
{
    public string Label { get; init; } = "";
    public long Count { get; init; }
}

public sealed class VectorSearchClientTypeStatDto
{
    public string ClientType { get; init; } = "";
    public long VectorSearchRunCount { get; init; }
    public double? AverageMaxScore { get; init; }
}

public sealed class ToolSelectionDebugStatsDto
{
    public long TotalNonPlanSelections { get; init; }
    public long SelectionExceptionFallbackCount { get; init; }
    public long VectorSkippedNoEmbeddingCount { get; init; }
    public long VectorSkippedNonPersistentStoreCount { get; init; }
    public long VectorSearchRunCount { get; init; }
    /// <summary>检索结果 GoodEnough==true 的次数（可能因 Results 为空仍未走向量优先）。</summary>
    public long VectorGoodEnoughTrueCount { get; init; }
    /// <summary>检索结果 GoodEnough==false 的次数（与 VectorSearchRunCount、VectorGoodEnoughTrueCount 一致可核对）。</summary>
    public long VectorGoodEnoughFalseCount { get; init; }
    /// <summary>理论上极少：GoodEnough 为 true 但去重命中数为 0。</summary>
    public long VectorGoodEnoughTrueButEmptyResultsCount { get; init; }
    public long VectorFirstPathChosenCount { get; init; }
    /// <summary>执行了向量检索但未采用向量优先（未达 goodEnough 或无命中），通常随后两阶段。</summary>
    public long VectorSearchButTwoStageCount { get; init; }
    public long TwoStageInvocationsCount { get; init; }
    /// <summary>已执行向量检索且随后调用了两阶段子类选择的次数（向量未达优先路径后的第二轮）。</summary>
    public long VectorThenTwoStageCount { get; init; }
    /// <summary>上述次数中，两阶段结束后仍为全量工具（SelectedPairs 为空）的次数。</summary>
    public long VectorThenTwoStageFullToolsCount { get; init; }
    /// <summary>全量工具次数 / 向量后再两阶段次数；分母为 0 时为 null。</summary>
    public double? VectorThenTwoStageFullToolsRate { get; init; }
    public double? VectorFirstPathRateAmongVectorSearches { get; init; }
    public double? VectorFirstPathRateAmongSelections { get; init; }
    public double? TwoStageRateAmongSelections { get; init; }
    public double? VectorGoodEnoughFalseRateAmongVectorSearches { get; init; }
    public double? AverageMaxScoreAmongVectorSearches { get; init; }
    public double? AverageDistinctHitCountAmongVectorSearches { get; init; }
    public double? AverageTop1MinusTop2AmongVectorSearches { get; init; }
    public long Top1MinusTop2SampleCount { get; init; }
    public List<VectorScoreHistogramBucketDto> MaxScoreHistogram { get; init; } = new();
    public List<VectorSearchClientTypeStatDto> VectorSearchByClientType { get; init; } = new();
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
