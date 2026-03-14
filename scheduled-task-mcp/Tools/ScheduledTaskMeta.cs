namespace ScheduledTaskMcp.Tools;

internal class ScheduledTaskMeta
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ScheduleType { get; set; } = "cron";
    public string? CronExpression { get; set; }
    public int? IntervalMinutes { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public bool Enabled { get; set; } = true;
    public string? TimeZone { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public int? MaxRuns { get; set; }
    public bool DeleteAfterRun { get; set; }
    public int RunCount { get; set; }
}
