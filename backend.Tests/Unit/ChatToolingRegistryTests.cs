using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class ChatToolingRegistryTests
{
    [Fact]
    public async Task ExecuteAsync_invokes_registered_operation()
    {
        var reg = new ChatToolingRegistry(NullLogger<ChatToolingRegistry>.Instance);
        var id = "t1";
        var called = false;
        reg.Register(id, () =>
        {
            called = true;
            return Task.CompletedTask;
        });
        await reg.ExecuteToolingPhaseAsync(id);
        reg.Unregister(id);
        Assert.True(called);
    }

    [Fact]
    public async Task ExecuteAsync_unknown_id_is_noop()
    {
        var reg = new ChatToolingRegistry(NullLogger<ChatToolingRegistry>.Instance);
        await reg.ExecuteToolingPhaseAsync("missing");
        Assert.True(true);
    }
}
