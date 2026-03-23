using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class UserOptionsManagerTests
{
    [Fact]
    public async Task RequestOptionsAsync_CompletesWhenHandleResponse_WithSelections()
    {
        string? capturedId = null;
        var mgr = new UserOptionsManager(
            async (sessionId, message) =>
            {
                Assert.Equal("session-a", sessionId);
                using var doc = JsonDocument.Parse(message);
                Assert.Equal("ask_options_request", doc.RootElement.GetProperty("type").GetString());
                capturedId = doc.RootElement.GetProperty("id").GetString();
                await Task.CompletedTask;
            },
            NullLogger<UserOptionsManager>.Instance,
            requestTimeout: TimeSpan.FromMinutes(2));

        var steps = new List<AskOptionsStep>
        {
            new("step1", "Pick one", new List<AskOptionsOption> { new("optA", "A"), new("optB", "B") })
        };

        var requestTask = mgr.RequestOptionsAsync("session-a", "Title", "Prompt", steps);
        Assert.NotNull(capturedId);
        mgr.HandleResponse(capturedId!, new Dictionary<string, string> { ["step1"] = "optB" });

        var result = await requestTask;
        Assert.False(result.TimedOut);
        Assert.Equal("optB", result.Selections["step1"]);
    }

    [Fact]
    public async Task RequestOptionsAsync_TimedOut_ReturnsTimedOutWithEmptySelections()
    {
        var mgr = new UserOptionsManager(
            (_, _) => Task.CompletedTask,
            NullLogger<UserOptionsManager>.Instance,
            requestTimeout: TimeSpan.FromMilliseconds(80));

        var steps = new List<AskOptionsStep>
        {
            new("s1", "Q", new List<AskOptionsOption> { new("x", "X") })
        };

        var result = await mgr.RequestOptionsAsync("sid", "", "", steps);
        Assert.True(result.TimedOut);
        Assert.Empty(result.Selections);
    }

    [Fact]
    public async Task RequestOptionsAsync_CancellationTokenCanceled_ThrowsOperationCanceledException()
    {
        var mgr = new UserOptionsManager(
            (_, _) => Task.CompletedTask,
            NullLogger<UserOptionsManager>.Instance,
            requestTimeout: TimeSpan.FromMinutes(1));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var steps = new List<AskOptionsStep>
        {
            new("s1", "Q", new List<AskOptionsOption> { new("x", "X") })
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mgr.RequestOptionsAsync("sid", "", "", steps, cts.Token));
    }

    [Fact]
    public void HandleResponse_UnknownId_DoesNotThrow()
    {
        var mgr = new UserOptionsManager(
            (_, _) => Task.CompletedTask,
            NullLogger<UserOptionsManager>.Instance);
        mgr.HandleResponse("nonexistent", new Dictionary<string, string> { ["a"] = "b" });
    }
}
