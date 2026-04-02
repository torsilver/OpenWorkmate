using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ToolInvocationTurnMeterTests
{
    private const string Sid = "unit-test-meter-session";

    private static void ClearSession()
    {
        SessionContext.SetSessionId(null);
    }

    [Fact]
    public async Task Count_accumulates_when_parallel_tasks_set_session()
    {
        ToolInvocationTurnMeter.BeginTurn(Sid);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                SessionContext.SetSessionId(Sid);
                ToolInvocationTurnMeter.RecordInvocation();
            }))).ConfigureAwait(true);
            Assert.Equal(5, ToolInvocationTurnMeter.GetCount(Sid));
        }
        finally
        {
            ToolInvocationTurnMeter.EndTurn(Sid);
            ClearSession();
        }
    }

    [Fact]
    public void ResetCount_zeros_while_active()
    {
        ToolInvocationTurnMeter.BeginTurn(Sid);
        try
        {
            SessionContext.SetSessionId(Sid);
            ToolInvocationTurnMeter.RecordInvocation();
            Assert.Equal(1, ToolInvocationTurnMeter.GetCount(Sid));
            ToolInvocationTurnMeter.ResetCount(Sid);
            Assert.Equal(0, ToolInvocationTurnMeter.GetCount(Sid));
        }
        finally
        {
            ToolInvocationTurnMeter.EndTurn(Sid);
            ClearSession();
        }
    }

    [Fact]
    public void GetCount_zero_after_EndTurn()
    {
        ToolInvocationTurnMeter.BeginTurn(Sid);
        SessionContext.SetSessionId(Sid);
        ToolInvocationTurnMeter.RecordInvocation();
        ToolInvocationTurnMeter.EndTurn(Sid);
        ClearSession();
        Assert.Equal(0, ToolInvocationTurnMeter.GetCount(Sid));
    }

    [Fact]
    public void RecordInvocation_ignored_without_active_BeginTurn()
    {
        ToolInvocationTurnMeter.EndTurn(Sid);
        SessionContext.SetSessionId(Sid);
        ToolInvocationTurnMeter.RecordInvocation();
        ClearSession();
        Assert.Equal(0, ToolInvocationTurnMeter.GetCount(Sid));
    }
}
