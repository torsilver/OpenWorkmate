namespace OfficeCopilot.Server.Services.ScheduledTask;

/// <summary>根据 meta 计算下次执行时间。</summary>
public static class CronNextRun
{
    public static DateTimeOffset? GetNextRunAt(ScheduledTaskMeta meta, DateTimeOffset fromUtc)
    {
        if (string.Equals(meta.ScheduleType, "interval", StringComparison.OrdinalIgnoreCase) && meta.IntervalMinutes.HasValue && meta.IntervalMinutes.Value > 0)
        {
            var baseTime = meta.LastRunAt ?? fromUtc;
            return baseTime.AddMinutes(meta.IntervalMinutes.Value);
        }
        if (string.Equals(meta.ScheduleType, "cron", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(meta.CronExpression))
        {
            try
            {
                var cron = Cronos.CronExpression.Parse(meta.CronExpression, Cronos.CronFormat.Standard);
                var tz = string.IsNullOrWhiteSpace(meta.TimeZone)
                    ? TimeZoneInfo.Utc
                    : TimeZoneInfo.FindSystemTimeZoneById(meta.TimeZone.Trim());
                var from = fromUtc.UtcDateTime;
                var next = cron.GetNextOccurrence(from, tz);
                return next.HasValue ? new DateTimeOffset(next.Value, tz.GetUtcOffset(next.Value)) : (DateTimeOffset?)null;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
