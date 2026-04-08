using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AgentDebugStatsServiceTests
{
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
            s.RecordTwoStageUsed();
            s.RecordToolInvocation("Word", "read", success: true);
            s.RecordToolInvocation("Word", "read", success: false);
            s.Reset();
            var snap = s.GetSnapshot();
            Assert.Equal(0, snap.ToolSelection.TotalNonPlanSelections);
            Assert.Equal(0, snap.ToolSelection.TwoStageInvocationsCount);
            Assert.Equal(0, snap.ToolSelection.ToolNeedGateLlmInvocationCount);
            Assert.Equal(0, snap.ToolSelection.ToolNeedGateChatOnlyCount);
            Assert.Empty(snap.ToolInvocations);
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
    public void TwoStageRate_DerivedFromTotals()
    {
        var path = NewTempPersistencePath();
        try
        {
            var s = CreateService(path);
            s.IncrementToolSelectionTotal();
            s.IncrementToolSelectionTotal();
            s.RecordTwoStageUsed();
            var ts = s.GetSnapshot().ToolSelection;
            Assert.Equal(2, ts.TotalNonPlanSelections);
            Assert.Equal(1, ts.TwoStageInvocationsCount);
            Assert.NotNull(ts.TwoStageRateAmongSelections);
            Assert.Equal(0.5, ts.TwoStageRateAmongSelections!.Value, 3);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void Persistence_RoundTrip_RestoresTwoStageAndToolInvocations()
    {
        var path = NewTempPersistencePath();
        try
        {
            {
                var s = CreateService(path);
                s.IncrementToolSelectionTotal();
                s.RecordTwoStageUsed();
                s.RecordToolInvocation("CLI", "run_command", true);
                s.RecordToolInvocation("CLI", "run_command", false);
                s.FlushPersistenceForTests();
                Assert.NotNull(s.GetSnapshot().StatsAccumulatedSinceUtc);
            }

            {
                var s2 = CreateService(path);
                var snap = s2.GetSnapshot();
                Assert.NotNull(snap.StatsAccumulatedSinceUtc);
                Assert.Equal(1, snap.ToolSelection.TotalNonPlanSelections);
                Assert.Equal(1, snap.ToolSelection.TwoStageInvocationsCount);
                var row = Assert.Single(snap.ToolInvocations);
                Assert.Equal("CLI.run_command", row.ToolId);
                Assert.Equal(1, row.SuccessCount);
                Assert.Equal(1, row.FailCount);
                Assert.Equal(2, row.TotalCalls);
            }
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void Persistence_Version1File_StartsFresh()
    {
        var path = NewTempPersistencePath();
        try
        {
            File.WriteAllText(path, """{"version":1,"toolSelectionTotal":99,"toolInvocations":[]}""");
            var s = CreateService(path);
            var snap = s.GetSnapshot();
            Assert.Equal(0, snap.ToolSelection.TotalNonPlanSelections);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void Persistence_Version2File_StartsFresh()
    {
        var path = NewTempPersistencePath();
        try
        {
            File.WriteAllText(path, """{"version":2,"toolSelectionTotal":3,"twoStageInvocationsCount":1,"toolInvocations":[]}""");
            var s = CreateService(path);
            Assert.Equal(0, s.GetSnapshot().ToolSelection.TotalNonPlanSelections);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public void ToolNeedGateStats_RoundTrip()
    {
        var path = NewTempPersistencePath();
        try
        {
            {
                var s = CreateService(path);
                s.RecordToolNeedGateLlmInvocation();
                s.RecordToolNeedGateLlmInvocation();
                s.RecordToolNeedGateChatOnly();
                s.FlushPersistenceForTests();
            }
            {
                var s2 = CreateService(path);
                var ts = s2.GetSnapshot().ToolSelection;
                Assert.Equal(2, ts.ToolNeedGateLlmInvocationCount);
                Assert.Equal(1, ts.ToolNeedGateChatOnlyCount);
                Assert.NotNull(ts.ToolNeedGateChatOnlyRateAmongGateLlm);
                Assert.Equal(0.5, ts.ToolNeedGateChatOnlyRateAmongGateLlm!.Value, 3);
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
