using System.Collections.Generic;
using System.Linq;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 进程内调试统计：工具向量选择路径、各工具调用成功/失败次数。随服务重启清空。
/// </summary>
public sealed class AgentDebugStatsService
{
    private readonly object _gate = new();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    private long _toolSelectionTotal;
    private long _toolSelectionException;
    private long _vectorSkippedNoEmbedding;
    private long _vectorSkippedNonPersistent;
    private long _vectorSearchRuns;
    private long _vectorGoodEnoughFlagTrue;
    private long _vectorFirstPathChosen;
    private long _vectorSearchRanButFallbackToTwoStage;
    private long _twoStageUsed;
    private readonly Dictionary<string, (long Success, long Fail)> _toolInvocations = new(StringComparer.Ordinal);

    public void IncrementToolSelectionTotal()
    {
        lock (_gate) { _toolSelectionTotal++; }
    }

    public void RecordToolSelectionException()
    {
        lock (_gate) { _toolSelectionException++; }
    }

    public void RecordVectorSkippedNoEmbedding()
    {
        lock (_gate) { _vectorSkippedNoEmbedding++; }
    }

    public void RecordVectorSkippedNonPersistent()
    {
        lock (_gate) { _vectorSkippedNonPersistent++; }
    }

    /// <param name="goodEnough">检索结果上的 GoodEnough 标志（与是否采用向量优先路径不同）。</param>
    public void RecordVectorSearchCompleted(bool goodEnough, bool vectorFirstPathChosen)
    {
        lock (_gate)
        {
            _vectorSearchRuns++;
            if (goodEnough)
                _vectorGoodEnoughFlagTrue++;
            if (vectorFirstPathChosen)
                _vectorFirstPathChosen++;
            else
                _vectorSearchRanButFallbackToTwoStage++;
        }
    }

    public void RecordTwoStageUsed()
    {
        lock (_gate) { _twoStageUsed++; }
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
            _toolInvocations.Clear();
        }
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

            return new AgentDebugStatsResponse
            {
                ServerStartedUtc = _startedUtc,
                ToolSelection = new ToolSelectionDebugStatsDto
                {
                    TotalNonPlanSelections = total,
                    SelectionExceptionFallbackCount = _toolSelectionException,
                    VectorSkippedNoEmbeddingCount = _vectorSkippedNoEmbedding,
                    VectorSkippedNonPersistentStoreCount = _vectorSkippedNonPersistent,
                    VectorSearchRunCount = vecRuns,
                    VectorGoodEnoughTrueCount = _vectorGoodEnoughFlagTrue,
                    VectorFirstPathChosenCount = vecFirst,
                    VectorSearchButTwoStageCount = _vectorSearchRanButFallbackToTwoStage,
                    TwoStageInvocationsCount = _twoStageUsed,
                    VectorFirstPathRateAmongVectorSearches = vecRuns > 0 ? (double)vecFirst / vecRuns : null,
                    VectorFirstPathRateAmongSelections = total > 0 ? (double)vecFirst / total : null,
                    TwoStageRateAmongSelections = total > 0 ? (double)_twoStageUsed / total : null
                },
                ToolInvocations = list
            };
        }
    }
}

public sealed class AgentDebugStatsResponse
{
    public DateTimeOffset ServerStartedUtc { get; init; }
    public ToolSelectionDebugStatsDto ToolSelection { get; init; } = new();
    public List<ToolInvocationDebugStatDto> ToolInvocations { get; init; } = new();
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
    public long VectorFirstPathChosenCount { get; init; }
    /// <summary>执行了向量检索但未采用向量优先（未达 goodEnough 或无命中），通常随后两阶段。</summary>
    public long VectorSearchButTwoStageCount { get; init; }
    public long TwoStageInvocationsCount { get; init; }
    public double? VectorFirstPathRateAmongVectorSearches { get; init; }
    public double? VectorFirstPathRateAmongSelections { get; init; }
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
