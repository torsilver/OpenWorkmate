using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AgentDebugStatsServiceTests
{
    [Fact]
    public void Reset_ClearsAllCountersAndToolRows()
    {
        var s = new AgentDebugStatsService();
        s.IncrementToolSelectionTotal();
        s.RecordVectorSearchCompleted(goodEnough: true, vectorFirstPathChosen: true);
        s.RecordToolInvocation("Word", "read", success: true);
        s.RecordToolInvocation("Word", "read", success: false);
        s.Reset();
        var snap = s.GetSnapshot();
        Assert.Equal(0, snap.ToolSelection.TotalNonPlanSelections);
        Assert.Equal(0, snap.ToolSelection.VectorSearchRunCount);
        Assert.Empty(snap.ToolInvocations);
    }

    [Fact]
    public void RecordToolInvocation_ComputesSuccessRate()
    {
        var s = new AgentDebugStatsService();
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

    [Fact]
    public void VectorRates_DerivedFromTotals()
    {
        var s = new AgentDebugStatsService();
        s.IncrementToolSelectionTotal();
        s.IncrementToolSelectionTotal();
        s.RecordVectorSearchCompleted(goodEnough: true, vectorFirstPathChosen: true);
        s.RecordVectorSearchCompleted(goodEnough: false, vectorFirstPathChosen: false);
        var ts = s.GetSnapshot().ToolSelection;
        Assert.Equal(2, ts.TotalNonPlanSelections);
        Assert.Equal(2, ts.VectorSearchRunCount);
        Assert.Equal(1, ts.VectorFirstPathChosenCount);
        Assert.NotNull(ts.VectorFirstPathRateAmongVectorSearches);
        Assert.Equal(0.5, ts.VectorFirstPathRateAmongVectorSearches!.Value, 3);
        Assert.NotNull(ts.VectorFirstPathRateAmongSelections);
        Assert.Equal(0.5, ts.VectorFirstPathRateAmongSelections!.Value, 3);
    }
}
