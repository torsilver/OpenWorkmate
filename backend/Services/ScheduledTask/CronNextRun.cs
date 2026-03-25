namespace OfficeCopilot.Server.Services.ScheduledTask;

/// <summary>根据 meta 计算下次执行时间。</summary>
public static class CronNextRun
{
    public static DateTimeOffset? GetNextRunAt(ScheduledTaskMeta meta, DateTimeOffset fromUtc)
    {
        // 单次任务：不通过此处排下一次；首次 NextRunAt 在创建时写入，执行后由 Runner 删除。
        if (string.Equals(meta.ScheduleType, "once", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(meta.ScheduleType, "interval", StringComparison.OrdinalIgnoreCase))
        {
            var baseTime = meta.LastRunAt ?? fromUtc;
            if (meta.IntervalSeconds.HasValue && meta.IntervalSeconds.Value > 0)
                return baseTime.AddSeconds(meta.IntervalSeconds.Value);
            if (meta.IntervalMinutes.HasValue && meta.IntervalMinutes.Value > 0)
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
