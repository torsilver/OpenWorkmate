using OfficeCopilot.Server.Services.ScheduledTask;
using Xunit;

namespace backend.Tests.Unit;

public class ScheduledTaskSchedulingTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryResolveOnceFirstRun_IsoFuture_UsesParsedTime()
    {
        var iso = "2025-12-31T00:00:00Z";
        Assert.True(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, iso, null, null, null, out var next, out var err));
        Assert.Null(err);
        Assert.Equal(DateTimeOffset.Parse(iso), next);
    }

    [Fact]
    public void TryResolveOnceFirstRun_IsoPast_UsesNow()
    {
        var iso = "2020-01-01T00:00:00Z";
        Assert.True(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, iso, null, null, null, out var next, out _));
        Assert.Equal(Now, next);
    }

    [Fact]
    public void TryResolveOnceFirstRun_DtoFuture_UsesDto()
    {
        var dto = Now.AddHours(2);
        Assert.True(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, null, dto, null, null, out var next, out _));
        Assert.Equal(dto, next);
    }

    [Fact]
    public void TryResolveOnceFirstRun_IntervalSeconds_AddsDelay()
    {
        Assert.True(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, null, null, 90, null, out var next, out _));
        Assert.Equal(Now.AddSeconds(90), next);
    }

    [Fact]
    public void TryResolveOnceFirstRun_IntervalMinutes_AddsDelay()
    {
        Assert.True(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, null, null, null, 3, out var next, out _));
        Assert.Equal(Now.AddMinutes(3), next);
    }

    [Fact]
    public void TryResolveOnceFirstRun_None_ReturnsFalseWithMessage()
    {
        Assert.False(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, null, null, null, null, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains("once", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolveOnceFirstRun_IsoAndSeconds_MutuallyExclusive()
    {
        Assert.False(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, "2026-01-01T00:00:00Z", null, 10, null, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains("互斥", err);
    }

    [Fact]
    public void TryResolveOnceFirstRun_DtoAndMinutes_MutuallyExclusive()
    {
        Assert.False(ScheduledTaskScheduling.TryResolveOnceFirstRun(Now, null, Now.AddDays(1), null, 1, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains("互斥", err);
    }
}
