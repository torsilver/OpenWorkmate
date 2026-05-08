using System.Text.Json.Serialization;

namespace OpenWorkmate.Server.Services.ScheduledTask;

/// <summary>定时任务元数据：调度与状态。</summary>
public class ScheduledTaskMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("scheduleType")]
    public string ScheduleType { get; set; } = "cron"; // cron | interval | once

    /// <summary>When scheduleType is once: absolute first-fire time (informational / update); initial <see cref="NextRunAt"/> is derived at create.</summary>
    [JsonPropertyName("runAt")]
    public DateTimeOffset? RunOnceAt { get; set; }

    [JsonPropertyName("cronExpression")]
    public string? CronExpression { get; set; }

    [JsonPropertyName("intervalMinutes")]
    public int? IntervalMinutes { get; set; }

    /// <summary>When scheduleType is interval: repeat every N seconds. If &gt; 0, takes precedence over <see cref="IntervalMinutes"/> in scheduling.</summary>
    [JsonPropertyName("intervalSeconds")]
    public int? IntervalSeconds { get; set; }

    [JsonPropertyName("nextRunAt")]
    public DateTimeOffset? NextRunAt { get; set; }

    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }

    [JsonPropertyName("endAt")]
    public DateTimeOffset? EndAt { get; set; }

    [JsonPropertyName("maxRuns")]
    public int? MaxRuns { get; set; }

    [JsonPropertyName("deleteAfterRun")]
    public bool DeleteAfterRun { get; set; }

    [JsonPropertyName("runCount")]
    public int RunCount { get; set; }
}
