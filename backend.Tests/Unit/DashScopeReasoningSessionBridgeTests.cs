using System.Collections.Concurrent;
using OfficeCopilot.Server.Services.DashScope;
using Xunit;

namespace backend.Tests.Unit;

public sealed class DashScopeReasoningSessionBridgeTests
{
    [Fact]
    public void Attach_Drain_DequeuesFifo()
    {
        var sid = Guid.NewGuid().ToString("N");
        var q = new ConcurrentQueue<string>();
        DashScopeReasoningSessionBridge.AttachQueue(sid, q);
        q.Enqueue("a");
        q.Enqueue("b");
        Assert.Equal(["a", "b"], DashScopeReasoningSessionBridge.DrainForSession(sid).ToArray());
        Assert.Empty(DashScopeReasoningSessionBridge.DrainForSession(sid));
    }

    [Fact]
    public void TryDetach_RemovesOnlyWhenQueueStillCurrent()
    {
        var sid = Guid.NewGuid().ToString("N");
        var q1 = new ConcurrentQueue<string>();
        DashScopeReasoningSessionBridge.AttachQueue(sid, q1);
        DashScopeReasoningSessionBridge.TryDetachQueue(sid, q1);

        var q2 = new ConcurrentQueue<string>();
        DashScopeReasoningSessionBridge.AttachQueue(sid, q2);
        q2.Enqueue("x");
        Assert.Equal(["x"], DashScopeReasoningSessionBridge.DrainForSession(sid).ToArray());
        DashScopeReasoningSessionBridge.TryDetachQueue(sid, q2);
    }
}
