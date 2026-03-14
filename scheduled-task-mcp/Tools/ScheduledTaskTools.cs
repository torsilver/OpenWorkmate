using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cronos;
using ModelContextProtocol.Server;
using ScheduledTaskMcp;

namespace ScheduledTaskMcp.Tools;

internal class ScheduledTaskTools
{
    private readonly ScheduledTaskOptions _options;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public ScheduledTaskTools(Microsoft.Extensions.Options.IOptions<ScheduledTaskOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    private string GetRootDirectory()
    {
        var dir = _options.Directory?.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dir = Path.Combine(appData, "OfficeCopilot", "ScheduledTasks");
        }
        dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir));
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    private static string SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var s = Regex.Replace(id.Trim(), @"[^\w\-]", "_");
        return s.Length > 0 ? s : "";
    }

    private void EnsurePathInRoot(string fullPath)
    {
        var root = GetRootDirectory();
        var normalized = Path.GetFullPath(fullPath);
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path must be within the scheduled tasks directory.");
    }

    private static DateTimeOffset? GetNextRunAt(ScheduledTaskMeta meta, DateTimeOffset fromUtc)
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
                var cron = CronExpression.Parse(meta.CronExpression, CronFormat.Standard);
                var tz = string.IsNullOrWhiteSpace(meta.TimeZone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(meta.TimeZone.Trim());
                var next = cron.GetNextOccurrence(fromUtc.UtcDateTime, tz);
                return next.HasValue ? new DateTimeOffset(next.Value, tz.GetUtcOffset(next.Value)) : (DateTimeOffset?)null;
            }
            catch { return null; }
        }
        return null;
    }

    [McpServerTool]
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
        var safeId = SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            safeId = Guid.NewGuid().ToString("N")[..12];
        var root = GetRootDirectory();
        var mdPath = Path.Combine(root, safeId + ".task.md");
        EnsurePathInRoot(mdPath);
        await File.WriteAllTextAsync(mdPath, content ?? "", cancellationToken).ConfigureAwait(false);
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
        meta.NextRunAt = GetNextRunAt(meta, DateTimeOffset.UtcNow);
        if (meta.ScheduleType.Equals("interval", StringComparison.OrdinalIgnoreCase) && meta.NextRunAt == null && meta.IntervalMinutes.HasValue)
            meta.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(meta.IntervalMinutes.Value);
        var metaPath = Path.Combine(root, safeId + ".meta.json");
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOptions), cancellationToken).ConfigureAwait(false);
        return $"[OK] Scheduled task created: id={safeId}, nextRunAt={meta.NextRunAt?.ToString("O") ?? "-"}.";
    }

    [McpServerTool]
    [Description("List scheduled tasks. Returns id, title, nextRunAt, enabled for each.")]
    public async Task<string> ScheduledTaskListAsync(
        [Description("If true, only return enabled tasks")] bool enabledOnly = false,
        CancellationToken cancellationToken = default)
    {
        var root = GetRootDirectory();
        if (!Directory.Exists(root)) return "(no tasks)";
        var sb = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*.meta.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var meta = JsonSerializer.Deserialize<ScheduledTaskMeta>(json, JsonOptions);
                if (meta == null) continue;
                if (enabledOnly && !meta.Enabled) continue;
                sb.AppendLine($"- {meta.Id} | {meta.Title} | next: {meta.NextRunAt?.ToString("O") ?? "-"} | enabled: {meta.Enabled}");
            }
            catch { }
        }
        return sb.Length == 0 ? "(no tasks)" : sb.ToString();
    }

    [McpServerTool]
    [Description("Read one scheduled task by id. Returns task content and meta.")]
    public async Task<string> ScheduledTaskReadAsync(
        [Description("Task id")] string id,
        CancellationToken cancellationToken = default)
    {
        var safeId = SanitizeId(id);
        if (string.IsNullOrEmpty(safeId)) return "[Error] id required.";
        var root = GetRootDirectory();
        var mdPath = Path.Combine(root, safeId + ".task.md");
        if (!File.Exists(mdPath)) return $"[Not found] id={safeId}.";
        EnsurePathInRoot(mdPath);
        var content = await File.ReadAllTextAsync(mdPath, cancellationToken).ConfigureAwait(false);
        var metaPath = Path.Combine(root, safeId + ".meta.json");
        var metaJson = File.Exists(metaPath) ? await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false) : "{}";
        return $"--- meta ---\n{metaJson}\n--- content ---\n{content}";
    }

    [McpServerTool]
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
        var safeId = SanitizeId(id);
        if (string.IsNullOrEmpty(safeId)) return "[Error] id required.";
        var root = GetRootDirectory();
        var mdPath = Path.Combine(root, safeId + ".task.md");
        var metaPath = Path.Combine(root, safeId + ".meta.json");
        if (!File.Exists(metaPath)) return $"[Not found] id={safeId}.";
        EnsurePathInRoot(metaPath);
        var metaJson = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
        var meta = JsonSerializer.Deserialize<ScheduledTaskMeta>(metaJson, JsonOptions) ?? new ScheduledTaskMeta { Id = safeId };
        if (title != null) meta.Title = title.Trim();
        if (scheduleType != null) meta.ScheduleType = scheduleType.Trim();
        if (cronExpression != null) meta.CronExpression = string.IsNullOrWhiteSpace(cronExpression) ? null : cronExpression.Trim();
        if (intervalMinutes != null) meta.IntervalMinutes = intervalMinutes;
        if (enabled.HasValue) meta.Enabled = enabled.Value;
        meta.NextRunAt = GetNextRunAt(meta, DateTimeOffset.UtcNow);
        if (meta.ScheduleType.Equals("interval", StringComparison.OrdinalIgnoreCase) && meta.NextRunAt == null && meta.IntervalMinutes.HasValue)
            meta.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(meta.IntervalMinutes.Value);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOptions), cancellationToken).ConfigureAwait(false);
        if (content != null)
        {
            EnsurePathInRoot(mdPath);
            await File.WriteAllTextAsync(mdPath, content, cancellationToken).ConfigureAwait(false);
        }
        return $"[OK] Updated task id={safeId}, nextRunAt={meta.NextRunAt?.ToString("O") ?? "-"}.";
    }

    [McpServerTool]
    [Description("Delete a scheduled task (removes .task.md and .meta.json).")]
    public Task<string> ScheduledTaskDeleteAsync(
        [Description("Task id")] string id,
        CancellationToken cancellationToken = default)
    {
        var safeId = SanitizeId(id);
        if (string.IsNullOrEmpty(safeId)) return Task.FromResult("[Error] id required.");
        var root = GetRootDirectory();
        var mdPath = Path.Combine(root, safeId + ".task.md");
        var metaPath = Path.Combine(root, safeId + ".meta.json");
        var ok = false;
        if (File.Exists(mdPath)) { EnsurePathInRoot(mdPath); File.Delete(mdPath); ok = true; }
        if (File.Exists(metaPath)) { EnsurePathInRoot(metaPath); File.Delete(metaPath); }
        return Task.FromResult(ok ? $"[OK] Deleted id={safeId}." : $"[Not found] id={safeId}.");
    }
}
