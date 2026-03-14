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
    [Description("Create a scheduled task. Writes .task.md (content) and .meta.json. Backend will run the task at nextRunAt and send the MD to AI. scheduleType: 'cron' or 'interval'. For cron use cronExpression (e.g. '0 9 * * 1-5'); for interval use intervalMinutes.")]
    public async Task<string> ScheduledTaskCreateAsync(
        [Description("Unique id (alphanumeric, dash, underscore). Leave empty to auto-generate.")] string? id,
        [Description("Short title for the task")] string title,
        [Description("Markdown content describing what the AI should do when the task runs")] string content,
        [Description("'cron' or 'interval'")] string scheduleType = "cron",
        [Description("Cron expression (5 fields), e.g. '0 9 * * 1-5' for weekdays 9:00")] string? cronExpression = null,
        [Description("When scheduleType is 'interval', run every N minutes")] int? intervalMinutes = null,
        [Description("Optional timezone id, e.g. 'China Standard Time'")] string? timeZone = null,
        [Description("Optional end date for recurring task")] string? endAt = null,
        [Description("Optional max number of runs")] int? maxRuns = null,
        [Description("If true, delete task after one run (one-shot)")] bool deleteAfterRun = false,
        CancellationToken cancellationToken = default)
    {
        var safeId = FileScheduledTaskStore.SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            safeId = Guid.NewGuid().ToString("N")[..12];
        var meta = new ScheduledTaskMeta
        {
            Id = safeId,
            Title = title?.Trim() ?? safeId,
            ScheduleType = (scheduleType ?? "cron").Trim(),
            CronExpression = cronExpression?.Trim(),
            IntervalMinutes = intervalMinutes,
            Enabled = true,
            TimeZone = timeZone?.Trim(),
            EndAt = !string.IsNullOrWhiteSpace(endAt) && DateTimeOffset.TryParse(endAt, out var ea) ? ea : null,
            MaxRuns = maxRuns,
            DeleteAfterRun = deleteAfterRun
        };
        meta.NextRunAt = CronNextRun.GetNextRunAt(meta, DateTimeOffset.UtcNow);
        if (meta.ScheduleType.Equals("interval", StringComparison.OrdinalIgnoreCase) && meta.NextRunAt == null && meta.IntervalMinutes.HasValue)
            meta.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(meta.IntervalMinutes.Value);
        var savedId = await _store.SaveAsync(safeId, content ?? "", meta, cancellationToken).ConfigureAwait(false);
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
    [Description("Update a scheduled task. Omit optional params to keep current value.")]
    public async Task<string> ScheduledTaskUpdateAsync(
        [Description("Task id")] string id,
        [Description("New title")] string? title = null,
        [Description("New markdown content")] string? content = null,
        [Description("New schedule type")] string? scheduleType = null,
        [Description("New cron expression")] string? cronExpression = null,
        [Description("New interval minutes")] int? intervalMinutes = null,
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
        if (title != null) meta.Title = title.Trim();
        if (scheduleType != null) meta.ScheduleType = scheduleType.Trim();
        if (cronExpression != null) meta.CronExpression = string.IsNullOrWhiteSpace(cronExpression) ? null : cronExpression.Trim();
        if (intervalMinutes != null) meta.IntervalMinutes = intervalMinutes;
        if (enabled.HasValue) meta.Enabled = enabled.Value;
        meta.NextRunAt = CronNextRun.GetNextRunAt(meta, DateTimeOffset.UtcNow);
        if (meta.ScheduleType.Equals("interval", StringComparison.OrdinalIgnoreCase) && meta.NextRunAt == null && meta.IntervalMinutes.HasValue)
            meta.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(meta.IntervalMinutes.Value);
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
