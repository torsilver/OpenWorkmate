using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services.SemanticKernel;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class SkStreamChatPhasedRegistryTests
{
    [Fact]
    public async Task ExecuteContextPhaseAsync_then_ExecuteToolingPhaseAsync_runs_in_registration_order()
    {
        var reg = new SkStreamChatToolingProcessRegistry(NullLogger<SkStreamChatToolingProcessRegistry>.Instance);
        var order = new List<string>();
        reg.Register("p1",
            () =>
            {
                order.Add("context");
                return Task.CompletedTask;
            },
            () =>
            {
                order.Add("tooling");
                return Task.CompletedTask;
            });
        await reg.ExecuteContextPhaseAsync("p1");
        await reg.ExecuteToolingPhaseAsync("p1");
        reg.Unregister("p1");
        Assert.Equal(new[] { "context", "tooling" }, order);
    }

    [Fact]
    public async Task ExecuteContextPhaseAsync_when_no_context_registered_is_noop()
    {
        var reg = new SkStreamChatToolingProcessRegistry(NullLogger<SkStreamChatToolingProcessRegistry>.Instance);
        reg.Register("t1", () => Task.CompletedTask);
        await reg.ExecuteContextPhaseAsync("t1");
        reg.Unregister("t1");
    }
}
