using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.ToolInvocation;
using Xunit;

namespace backend.Tests.Unit;

public class AgentDebugStatsFailurePhaseTests
{
    private static string NewTempPersistencePath() =>
        Path.Combine(Path.GetTempPath(), $"agent-debug-stats-{Guid.NewGuid():N}.json");

    [Fact]
    public void RecordToolInvocation_FailurePhases_AccumulateAndPersist()
    {
        var path = NewTempPersistencePath();
        try
        {
            {
                var s = new AgentDebugStatsService(NullLogger<AgentDebugStatsService>.Instance, path, applicationLifetime: null);
                s.RecordToolInvocation("X", "f", false, ToolInvocationFailureKind.Binding);
                s.RecordToolInvocation("X", "f", false, ToolInvocationFailureKind.Mcp);
                s.RecordToolInvocation("X", "f", false, ToolInvocationFailureKind.Business);
                s.FlushPersistenceForTests();
                var snap = s.GetSnapshot();
                Assert.Equal(1, snap.ToolSelection.ToolFailureBindingCount);
                Assert.Equal(1, snap.ToolSelection.ToolFailureMcpCount);
                Assert.Equal(1, snap.ToolSelection.ToolFailureBusinessCount);
            }
            {
                var s2 = new AgentDebugStatsService(NullLogger<AgentDebugStatsService>.Instance, path, applicationLifetime: null);
                var snap2 = s2.GetSnapshot();
                Assert.Equal(1, snap2.ToolSelection.ToolFailureBindingCount);
                Assert.Equal(1, snap2.ToolSelection.ToolFailureMcpCount);
                Assert.Equal(1, snap2.ToolSelection.ToolFailureBusinessCount);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
