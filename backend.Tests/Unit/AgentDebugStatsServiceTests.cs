using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AgentDebugStatsServiceTests
{
    private static VectorSearchTelemetry T(
        string? clientType = "chrome",
        double max = 0.5,
        double? second = null,
        int hits = 1,
        bool goodEnough = false,
        bool vectorFirst = false) =>
        new(clientType, max, second, hits, goodEnough, vectorFirst);

    private static string NewTempPersistencePath() =>
        Path.Combine(Path.GetTempPath(), $"agent-debug-stats-{Guid.NewGuid():N}.json");

    private static AgentDebugStatsService CreateService(string path) =>
        new(NullLogger<AgentDebugStatsService>.Instance, path, applicationLifetime: null);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* test cleanup */
        }
    }

    [Fact]
    public void Reset_ClearsAllCountersAndToolRows()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.IncrementToolSelectionTotal();
            s.RecordVectorSearchCompleted(T(goodEnough: true, vectorFirst: true));
            s.RecordToolInvocation("Word", "read", success: true);
            s.RecordToolInvocation("Word", "read", success: false);
            s.Reset();
            var snap = s.GetSnapshot();
            Assert.Equal(0, snap.ToolSelection.TotalNonPlanSelections);
            Assert.Equal(0, snap.ToolSelection.VectorSearchRunCount);
            Assert.Empty(snap.ToolInvocations);
            Assert.Equal(5, snap.ToolSelection.MaxScoreHistogram.Count);
            Assert.All(snap.ToolSelection.MaxScoreHistogram, b => Assert.Equal(0, b.Count));
            Assert.Null(snap.StatsAccumulatedSinceUtc);
            Assert.False(File.Exists(path));
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void RecordToolInvocation_ComputesSuccessRate()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.RecordToolInvocation("CLI", "run_command", true);
            s.RecordToolInvocation("CLI", "run_command", true);
            s.RecordToolInvocation("CLI", "run_command", false);
            var row = Assert.Single(s.GetSnapshot().ToolInvocations);
            Assert.Equal("CLI.run_command", row.ToolId);
            Assert.Equal(3, row.TotalCalls);
            Assert.Equal(2, row.SuccessCount);
            Assert.Equal(1, row.FailCount);
            Assert.NotNull(row.SuccessRate);
            Assert.True(Math.Abs(row.SuccessRate!.Value - 2.0 / 3.0) < 0.0001);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void VectorRates_DerivedFromTotals()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.IncrementToolSelectionTotal();
            s.IncrementToolSelectionTotal();
            s.RecordVectorSearchCompleted(T(goodEnough: true, vectorFirst: true));
            s.RecordVectorSearchCompleted(T(goodEnough: false, vectorFirst: false));
            var ts = s.GetSnapshot().ToolSelection;
            Assert.Equal(2, ts.TotalNonPlanSelections);
            Assert.Equal(2, ts.VectorSearchRunCount);
            Assert.Equal(1, ts.VectorFirstPathChosenCount);
            Assert.Equal(1, ts.VectorGoodEnoughFalseCount);
            Assert.NotNull(ts.VectorFirstPathRateAmongVectorSearches);
            Assert.Equal(0.5, ts.VectorFirstPathRateAmongVectorSearches!.Value, 3);
            Assert.NotNull(ts.VectorFirstPathRateAmongSelections);
            Assert.Equal(0.5, ts.VectorFirstPathRateAmongSelections!.Value, 3);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(0.65, 2)]
    [InlineData(0.75, 3)]
    [InlineData(0.9, 4)]
    public void MaxScoreToHistogramBucket_MatchesRanges(double maxScore, int expectedBucket) =>
        Assert.Equal(expectedBucket, AgentDebugStatsService.MaxScoreToHistogramBucket(maxScore));

    [Fact]
    public void RecordVectorSearchCompleted_AccumulatesHistogramAndAverages()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.RecordVectorSearchCompleted(T(max: 0.45, second: 0.1, hits: 2, goodEnough: false)); // bucket 0
            s.RecordVectorSearchCompleted(T(max: 0.72, second: 0.5, hits: 3, goodEnough: true)); // bucket 3, margin 0.22
            s.RecordVectorSearchCompleted(T("wps", max: 0.82, second: 0.8, hits: 2, goodEnough: true)); // bucket 4, margin 0.02

            var ts = s.GetSnapshot().ToolSelection;
            Assert.Equal(3, ts.VectorSearchRunCount);
            var hist = ts.MaxScoreHistogram;
            Assert.Equal(5, hist.Count);
            Assert.Equal(1, hist[0].Count);
            Assert.Equal(0, hist[1].Count);
            Assert.Equal(0, hist[2].Count);
            Assert.Equal(1, hist[3].Count);
            Assert.Equal(1, hist[4].Count);

            var expectedAvgMax = (0.45 + 0.72 + 0.82) / 3.0;
            Assert.NotNull(ts.AverageMaxScoreAmongVectorSearches);
            Assert.True(Math.Abs(ts.AverageMaxScoreAmongVectorSearches!.Value - expectedAvgMax) < 1e-9);

            Assert.NotNull(ts.AverageDistinctHitCountAmongVectorSearches);
            Assert.True(Math.Abs(ts.AverageDistinctHitCountAmongVectorSearches!.Value - (2 + 3 + 2) / 3.0) < 1e-9);

            Assert.Equal(3, ts.Top1MinusTop2SampleCount);
            var expectedMargin = ((0.45 - 0.1) + (0.72 - 0.5) + (0.82 - 0.8)) / 3.0;
            Assert.NotNull(ts.AverageTop1MinusTop2AmongVectorSearches);
            Assert.True(Math.Abs(ts.AverageTop1MinusTop2AmongVectorSearches!.Value - expectedMargin) < 1e-9);

            Assert.Equal(2, ts.VectorSearchByClientType.Count);
            var chrome = ts.VectorSearchByClientType.First(x => x.ClientType == "chrome");
            Assert.Equal(2, chrome.VectorSearchRunCount);
            Assert.True(Math.Abs(chrome.AverageMaxScore!.Value - (0.45 + 0.72) / 2.0) < 1e-9);
            var wps = ts.VectorSearchByClientType.First(x => x.ClientType == "wps");
            Assert.Equal(1, wps.VectorSearchRunCount);
            Assert.True(Math.Abs(wps.AverageMaxScore!.Value - 0.82) < 1e-9);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void RecordVectorSearchCompleted_SingleHit_DoesNotCountMargin()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.RecordVectorSearchCompleted(T(max: 0.8, second: null, hits: 1, goodEnough: true));
            var ts = s.GetSnapshot().ToolSelection;
            Assert.Equal(0, ts.Top1MinusTop2SampleCount);
            Assert.Null(ts.AverageTop1MinusTop2AmongVectorSearches);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void GoodEnoughTrueWithZeroHits_IncrementsAnomalyCounter()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.RecordVectorSearchCompleted(T(max: 0, second: null, hits: 0, goodEnough: true, vectorFirst: false));
            var ts = s.GetSnapshot().ToolSelection;
            Assert.Equal(1, ts.VectorGoodEnoughTrueButEmptyResultsCount);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void RecordVectorThenTwoStageOutcome_AccumulatesAndRate()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.RecordVectorThenTwoStageOutcome(endedWithFullTools: true);
            s.RecordVectorThenTwoStageOutcome(endedWithFullTools: false);
            s.RecordVectorThenTwoStageOutcome(endedWithFullTools: true);
            var ts = s.GetSnapshot().ToolSelection;
            Assert.Equal(3, ts.VectorThenTwoStageCount);
            Assert.Equal(2, ts.VectorThenTwoStageFullToolsCount);
            Assert.NotNull(ts.VectorThenTwoStageFullToolsRate);
            Assert.True(Math.Abs(ts.VectorThenTwoStageFullToolsRate!.Value - 2.0 / 3.0) < 1e-9);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void Persistence_RoundTrip_RestoresVectorAndToolInvocations()
    {
        var path = NewTempPersistencePath();
        try
        {
            {
                var s = CreateService(path);
                s.IncrementToolSelectionTotal();
                s.RecordToolInvocation("CLI", "run_command", true);
                s.RecordToolInvocation("CLI", "run_command", false);
                s.RecordVectorSearchCompleted(T(max: 0.71, second: 0.5, hits: 2, goodEnough: true, vectorFirst: true));
                s.RecordVectorThenTwoStageOutcome(endedWithFullTools: false);
                s.FlushPersistenceForTests();
                Assert.NotNull(s.GetSnapshot().StatsAccumulatedSinceUtc);
            }

            {
                var s2 = CreateService(path);
                var snap = s2.GetSnapshot();
                Assert.NotNull(snap.StatsAccumulatedSinceUtc);
                Assert.Equal(1, snap.ToolSelection.TotalNonPlanSelections);
                Assert.Equal(1, snap.ToolSelection.VectorSearchRunCount);
                Assert.Equal(1, snap.ToolSelection.VectorThenTwoStageCount);
                Assert.Equal(0, snap.ToolSelection.VectorThenTwoStageFullToolsCount);
                var row = Assert.Single(snap.ToolInvocations);
                Assert.Equal("CLI.run_command", row.ToolId);
                Assert.Equal(1, row.SuccessCount);
                Assert.Equal(1, row.FailCount);
                Assert.Equal(2, row.TotalCalls);
                var hist = snap.ToolSelection.MaxScoreHistogram;
                Assert.Equal(1, hist[3].Count);
            }
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void Persistence_AfterReset_SecondInstanceStartsEmpty()
    {
        var path = NewTempPersistencePath();
        try
        {
            {
                var s = CreateService(path);
                s.RecordToolInvocation("A", "b", true);
                s.FlushPersistenceForTests();
            }
            {
                var s = CreateService(path);
                s.Reset();
            }
            {
                var s2 = CreateService(path);
                var snap = s2.GetSnapshot();
                Assert.Empty(snap.ToolInvocations);
                Assert.Null(snap.StatsAccumulatedSinceUtc);
            }
        }
        finally
        {
            TryDeleteFile(path);
        }
    }
}
