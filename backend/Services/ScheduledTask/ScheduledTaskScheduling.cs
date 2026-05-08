namespace OpenWorkmate.Server.Services.ScheduledTask;

/// <summary>创建/更新单次任务时计算首次 <see cref="ScheduledTaskMeta.NextRunAt"/>。</summary>
public static class ScheduledTaskScheduling
{
    /// <summary>
    /// scheduleType 为 once 时：必须且仅能指定一种触发方式——runAt（ISO）或 runAtDto 或 intervalSeconds&gt;0 或 intervalMinutes&gt;0。
    /// 若绝对时间已过去，则使用 <paramref name="nowUtc"/>（尽快执行）。
    /// </summary>
    public static bool TryResolveOnceFirstRun(
        DateTimeOffset nowUtc,
        string? runAtIso,
        DateTimeOffset? runAtDto,
        int? intervalSeconds,
        int? intervalMinutes,
        out DateTimeOffset nextRunUtc,
        out string? error)
    {
        nextRunUtc = default;
        error = null;

        DateTimeOffset parsedIso = default;
        var hasIso = !string.IsNullOrWhiteSpace(runAtIso) && DateTimeOffset.TryParse(runAtIso.Trim(), out parsedIso);
        var hasDto = runAtDto.HasValue;
        var hasSec = intervalSeconds.HasValue && intervalSeconds.Value > 0;
        var hasMin = intervalMinutes.HasValue && intervalMinutes.Value > 0;

        var n = (hasIso ? 1 : 0) + (hasDto ? 1 : 0) + (hasSec ? 1 : 0) + (hasMin ? 1 : 0);
        if (n == 0)
        {
            error = "scheduleType 为 once 时须指定其一：runAt（绝对时间 ISO8601），或 intervalSeconds/intervalMinutes（从当前起的延迟，大于 0）。";
            return false;
        }
        if (n > 1)
        {
            error = "scheduleType 为 once 时，runAt 与 intervalSeconds/intervalMinutes 互斥，请只指定一种。";
            return false;
        }

        if (hasIso)
        {
            nextRunUtc = parsedIso <= nowUtc ? nowUtc : parsedIso;
            return true;
        }
        if (hasDto)
        {
            var t = runAtDto!.Value;
            nextRunUtc = t <= nowUtc ? nowUtc : t;
            return true;
        }
        if (hasSec)
        {
            nextRunUtc = nowUtc.AddSeconds(intervalSeconds!.Value);
            return true;
        }
        nextRunUtc = nowUtc.AddMinutes(intervalMinutes!.Value);
        return true;
    }
}
