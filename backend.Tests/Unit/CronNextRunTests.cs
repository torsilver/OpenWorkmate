using OfficeCopilot.Server.Services.ScheduledTask;
using Xunit;

namespace backend.Tests.Unit;

public class CronNextRunTests
{
    [Fact]
    public void GetNextRunAt_Interval_ReturnsBasePlusInterval()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalMinutes = 30,
            LastRunAt = null
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(from.AddMinutes(30), next);
    }

    [Fact]
    public void GetNextRunAt_Interval_UsesLastRunAtAsBaseWhenSet()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var lastRun = new DateTimeOffset(2025, 3, 14, 9, 0, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalMinutes = 15,
            LastRunAt = lastRun
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(lastRun.AddMinutes(15), next);
    }

    [Fact]
    public void GetNextRunAt_IntervalZero_ReturnsNull()
    {
        var from = DateTimeOffset.UtcNow;
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalMinutes = 0
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.Null(next);
    }

    [Fact]
    public void GetNextRunAt_IntervalNull_ReturnsNull()
    {
        var from = DateTimeOffset.UtcNow;
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalMinutes = null
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.Null(next);
    }

    [Fact]
    public void GetNextRunAt_Cron_ValidExpression_ReturnsNextOccurrence()
    {
        var from = new DateTimeOffset(2025, 3, 14, 8, 0, 0, TimeSpan.Zero); // Friday 08:00 UTC
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "cron",
            CronExpression = "0 9 * * 1-5", // 09:00 weekdays
            TimeZone = "UTC"
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(9, next!.Value.Hour);
        Assert.Equal(14, next.Value.Day);
    }

    [Fact]
    public void GetNextRunAt_Cron_InvalidExpression_ReturnsNull()
    {
        var from = DateTimeOffset.UtcNow;
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "cron",
            CronExpression = "invalid cron"
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.Null(next);
    }

    [Fact]
    public void GetNextRunAt_Cron_EmptyExpression_ReturnsNull()
    {
        var from = DateTimeOffset.UtcNow;
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "cron",
            CronExpression = ""
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.Null(next);
    }

    [Fact]
    public void GetNextRunAt_Once_ReturnsNull()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "once",
            NextRunAt = from,
            IntervalSeconds = 60
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.Null(next);
    }

    [Fact]
    public void GetNextRunAt_UnknownScheduleType_ReturnsNull()
    {
        var from = DateTimeOffset.UtcNow;
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "unknown"
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.Null(next);
    }

    [Fact]
    public void GetNextRunAt_IntervalCaseInsensitive()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "INTERVAL",
            IntervalMinutes = 60
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(from.AddMinutes(60), next);
    }

    [Fact]
    public void GetNextRunAt_IntervalSeconds_ReturnsBasePlusSeconds()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalSeconds = 45,
            LastRunAt = null
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(from.AddSeconds(45), next);
    }

    [Fact]
    public void GetNextRunAt_IntervalSeconds_PrecedenceOverMinutes()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalSeconds = 30,
            IntervalMinutes = 120,
            LastRunAt = null
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(from.AddSeconds(30), next);
    }

    [Fact]
    public void GetNextRunAt_IntervalSeconds_UsesLastRunAtAsBaseWhenSet()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var lastRun = new DateTimeOffset(2025, 3, 14, 9, 30, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalSeconds = 90,
            LastRunAt = lastRun
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(lastRun.AddSeconds(90), next);
    }

    [Fact]
    public void GetNextRunAt_IntervalSecondsZero_FallsBackToMinutes()
    {
        var from = new DateTimeOffset(2025, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var meta = new ScheduledTaskMeta
        {
            ScheduleType = "interval",
            IntervalSeconds = 0,
            IntervalMinutes = 5,
            LastRunAt = null
        };
        var next = CronNextRun.GetNextRunAt(meta, from);
        Assert.NotNull(next);
        Assert.Equal(from.AddMinutes(5), next);
    }
}
