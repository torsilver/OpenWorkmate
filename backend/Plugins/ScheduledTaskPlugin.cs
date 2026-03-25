using System.ComponentModel;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services.ScheduledTask;

namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 定时任务插件：供 AI 创建与管理定时任务（.task.md + meta）；到点后由 ScheduledTaskRunnerService 将任务内容发给 AI 执行。
/// 使用已有 IScheduledTaskStore，目录由配置 ScheduledTasksDirectory 决定。
/// </summary>
public sealed class ScheduledTaskPlugin
{
    private readonly IScheduledTaskStore _store;

    public ScheduledTaskPlugin(IScheduledTaskStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    [KernelFunction("scheduled_task_create")]
    [Description(
        "Create a scheduled task. Two kinds: (1) ONCE: scheduleType 'once' — run exactly one time. Provide runAt (ISO8601 absolute time) OR intervalSeconds/intervalMinutes (>0) as delay from now; do not pass both. Task is removed after run. (2) REPEATING: scheduleType 'interval' — repeat every intervalSeconds or intervalMinutes (recurring); OR scheduleType 'cron' with cronExpression. For interval repeating, deleteAfterRun defaults false. Backend runs at nextRunAt (backend CLI policy, no browser HITL). Omit id to auto-generate.")]
    public async Task<string> ScheduledTaskCreateAsync(
        [Description("Unique id (alphanumeric, dash, underscore). Omit or null to auto-generate.")] string? id = null,
        [Description("Short title for the task")] string? title = null,
        [Description("Markdown content describing what the AI should do when the task runs")] string? content = null,
        [Description("'once' (single run), 'interval' (repeat every N min/sec), or 'cron'")] string scheduleType = "cron",
        [Description("When scheduleType is 'cron', 5-field cron expression")] string? cronExpression = null,
        [Description("When scheduleType is 'interval' (repeating): every N whole minutes. For 'once' with delay: use as delay minutes (mutually exclusive with runAt and intervalSeconds).")] int? intervalMinutes = null,
        [Description("When scheduleType is 'interval' (repeating): every N seconds (precedence over intervalMinutes). For 'once': delay seconds (mutually exclusive with runAt and intervalMinutes).")] int? intervalSeconds = null,
        [Description("When scheduleType is 'once': absolute first run time as ISO8601 (mutually exclusive with intervalSeconds/intervalMinutes).")] string? runAt = null,
        [Description("Optional timezone id for cron, e.g. 'China Standard Time'")] string? timeZone = null,
        [Description("Optional end date for recurring task")] string? endAt = null,
        [Description("Optional max number of runs")] int? maxRuns = null,
        [Description("If true, delete after one run (for repeating types). Ignored for scheduleType 'once' (always one-shot).")] bool deleteAfterRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            return "[Error] title and content are required.";
        var safeId = FileScheduledTaskStore.SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            safeId = Guid.NewGuid().ToString("N")[..12];
        var st = (scheduleType ?? "cron").Trim();
        var now = DateTimeOffset.UtcNow;
        var meta = new ScheduledTaskMeta
        {
            Id = safeId,
            Title = title.Trim(),
            ScheduleType = st,
            CronExpression = cronExpression?.Trim(),
            IntervalMinutes = intervalMinutes,
            IntervalSeconds = intervalSeconds,
            Enabled = true,
            TimeZone = timeZone?.Trim(),
            EndAt = !string.IsNullOrWhiteSpace(endAt) && DateTimeOffset.TryParse(endAt, out var ea) ? ea : null,
            MaxRuns = maxRuns,
            DeleteAfterRun = deleteAfterRun
        };

        if (st.Equals("once", StringComparison.OrdinalIgnoreCase))
        {
            if (!ScheduledTaskScheduling.TryResolveOnceFirstRun(now, runAt, null, intervalSeconds, intervalMinutes, out var onceNext, out var onceErr))
                return "[Error] " + (onceErr ?? "once 参数无效");
            meta.RunOnceAt = onceNext;
            meta.NextRunAt = onceNext;
            meta.DeleteAfterRun = true;
            meta.CronExpression = null;
        }
        else
        {
            meta.RunOnceAt = null;
            meta.NextRunAt = CronNextRun.GetNextRunAt(meta, now);
            if (st.Equals("interval", StringComparison.OrdinalIgnoreCase) && meta.NextRunAt == null)
            {
                if (meta.IntervalSeconds.HasValue && meta.IntervalSeconds.Value > 0)
                    meta.NextRunAt = now.AddSeconds(meta.IntervalSeconds.Value);
                else if (meta.IntervalMinutes.HasValue && meta.IntervalMinutes.Value > 0)
                    meta.NextRunAt = now.AddMinutes(meta.IntervalMinutes.Value);
            }
        }

        var savedId = await _store.SaveAsync(safeId, content.Trim(), meta, cancellationToken).ConfigureAwait(false);
        return $"[OK] Scheduled task created: id={savedId}, nextRunAt={meta.NextRunAt?.ToString("O") ?? "-"}.";
    }

    [KernelFunction("scheduled_task_list")]
    [Description("List scheduled tasks. Returns id, title, nextRunAt, enabled for each.")]
    public async Task<string> ScheduledTaskListAsync(
        [Description("If true, only return enabled tasks")] bool enabledOnly = false,
        CancellationToken cancellationToken = default)
    {
        var list = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var filtered = enabledOnly ? list.Where(m => m.Enabled).ToList() : list.ToList();
        if (filtered.Count == 0)
            return "(no tasks)";
        var sb = new System.Text.StringBuilder();
        foreach (var meta in filtered)
            sb.AppendLine($"- {meta.Id} | {meta.Title} | next: {meta.NextRunAt?.ToString("O") ?? "-"} | enabled: {meta.Enabled}");
        return sb.ToString();
    }

    [KernelFunction("scheduled_task_read")]
    [Description("Read one scheduled task by id. Returns task content and meta.")]
    public async Task<string> ScheduledTaskReadAsync(
        [Description("Task id")] string id,
        CancellationToken cancellationToken = default)
    {
        var safeId = FileScheduledTaskStore.SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            return "[Error] id required.";
        var result = await _store.GetAsync(safeId, cancellationToken).ConfigureAwait(false);
        if (result == null)
            return $"[Not found] id={safeId}.";
        var (content, meta) = result.Value;
        var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = false });
        return $"--- meta ---\n{metaJson}\n--- content ---\n{content}";
    }

    [KernelFunction("scheduled_task_update")]
    [Description("Update a scheduled task. Omit optional params to keep current value. For scheduleType 'once', to change fire time pass runAt or intervalSeconds/intervalMinutes.")]
    public async Task<string> ScheduledTaskUpdateAsync(
        [Description("Task id")] string id,
        [Description("New title")] string? title = null,
        [Description("New markdown content")] string? content = null,
        [Description("New schedule type: once | interval | cron")] string? scheduleType = null,
        [Description("New cron expression")] string? cronExpression = null,
        [Description("New interval minutes (repeating or once delay)")] int? intervalMinutes = null,
        [Description("New interval seconds (repeating or once delay)")] int? intervalSeconds = null,
        [Description("For once: ISO8601 absolute run time")] string? runAt = null,
        [Description("Enable or disable")] bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var safeId = FileScheduledTaskStore.SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            return "[Error] id required.";
        var existing = await _store.GetAsync(safeId, cancellationToken).ConfigureAwait(false);
        if (existing == null)
            return $"[Not found] id={safeId}.";
        var meta = existing.Value.Meta;
        var newContent = content ?? existing.Value.Content;
        var prevType = meta.ScheduleType;
        if (title != null) meta.Title = title.Trim();
        if (scheduleType != null) meta.ScheduleType = scheduleType.Trim();
        if (cronExpression != null) meta.CronExpression = string.IsNullOrWhiteSpace(cronExpression) ? null : cronExpression.Trim();
        if (intervalMinutes != null) meta.IntervalMinutes = intervalMinutes;
        if (intervalSeconds != null) meta.IntervalSeconds = intervalSeconds;
        if (enabled.HasValue) meta.Enabled = enabled.Value;

        var now = DateTimeOffset.UtcNow;
        var becameOnce = meta.ScheduleType.Equals("once", StringComparison.OrdinalIgnoreCase)
            && !prevType.Equals("once", StringComparison.OrdinalIgnoreCase);
        var hasOnceFire = !string.IsNullOrWhiteSpace(runAt)
            || (intervalSeconds is { } rs && rs > 0)
            || (intervalMinutes is { } rm && rm > 0);

        if (meta.ScheduleType.Equals("once", StringComparison.OrdinalIgnoreCase))
        {
            meta.DeleteAfterRun = true;
            if (becameOnce || hasOnceFire)
            {
                if (becameOnce && !hasOnceFire)
                    return "[Error] 转为 once 时须提供 runAt 或 intervalSeconds/intervalMinutes（大于 0）之一。";
                if (!ScheduledTaskScheduling.TryResolveOnceFirstRun(now, runAt, null, intervalSeconds, intervalMinutes, out var onceNext, out var onceErr))
                    return "[Error] " + (onceErr ?? "once 参数无效");
                meta.RunOnceAt = onceNext;
                meta.NextRunAt = onceNext;
                meta.CronExpression = null;
            }
        }
        else
        {
            meta.RunOnceAt = null;
            meta.NextRunAt = CronNextRun.GetNextRunAt(meta, now);
            if (meta.ScheduleType.Equals("interval", StringComparison.OrdinalIgnoreCase) && meta.NextRunAt == null)
            {
                if (meta.IntervalSeconds.HasValue && meta.IntervalSeconds.Value > 0)
                    meta.NextRunAt = now.AddSeconds(meta.IntervalSeconds.Value);
                else if (meta.IntervalMinutes.HasValue && meta.IntervalMinutes.Value > 0)
                    meta.NextRunAt = now.AddMinutes(meta.IntervalMinutes.Value);
            }
        }

        await _store.SaveAsync(safeId, newContent, meta, cancellationToken).ConfigureAwait(false);
        return $"[OK] Updated task id={safeId}, nextRunAt={meta.NextRunAt?.ToString("O") ?? "-"}.";
    }

    [KernelFunction("scheduled_task_delete")]
    [Description("Delete a scheduled task (removes .task.md and .meta.json).")]
    public async Task<string> ScheduledTaskDeleteAsync(
        [Description("Task id")] string id,
        CancellationToken cancellationToken = default)
    {
        var safeId = FileScheduledTaskStore.SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            return "[Error] id required.";
        var deleted = await _store.DeleteAsync(safeId, cancellationToken).ConfigureAwait(false);
        return deleted ? $"[OK] Deleted id={safeId}." : $"[Not found] id={safeId}.";
    }
}
