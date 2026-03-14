using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services.ScheduledTask;

/// <summary>基于文件系统的定时任务存储：每份任务一个 .task.md 文件 + 同名的 .meta.json。</summary>
public sealed class FileScheduledTaskStore : IScheduledTaskStore
{
    private readonly string _directory;
    private readonly ILogger<FileScheduledTaskStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public FileScheduledTaskStore(string directory, ILogger<FileScheduledTaskStore> logger)
    {
        _directory = Path.GetFullPath(directory);
        _logger = logger;
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled tasks directory create failed: {Dir}", _directory);
        }
    }

    public async Task<IReadOnlyList<ScheduledTaskMeta>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<ScheduledTaskMeta>();
        if (!Directory.Exists(_directory)) return list;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.task.md"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
            var meta = await ReadMetaAsync(name, ct).ConfigureAwait(false);
            if (meta != null)
                list.Add(meta);
        }
        return list.OrderBy(m => m.NextRunAt ?? DateTimeOffset.MaxValue).ToList();
    }

    public async Task<(string Content, ScheduledTaskMeta Meta)?> GetAsync(string taskId, CancellationToken ct = default)
    {
        var safeId = SanitizeId(taskId);
        if (string.IsNullOrEmpty(safeId)) return null;
        var path = Path.Combine(_directory, safeId + ".task.md");
        if (!File.Exists(path)) return null;
        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var meta = await ReadMetaAsync(safeId, ct).ConfigureAwait(false)
            ?? new ScheduledTaskMeta { Id = safeId, Title = safeId, Enabled = true };
        return (content, meta);
    }

    public async Task<string> SaveAsync(string? taskId, string content, ScheduledTaskMeta? meta = null, CancellationToken ct = default)
    {
        var safeId = SanitizeId(taskId);
        if (string.IsNullOrEmpty(safeId))
            safeId = Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(_directory, safeId + ".task.md");
        await File.WriteAllTextAsync(path, content ?? "", ct).ConfigureAwait(false);
        var m = meta ?? new ScheduledTaskMeta { Id = safeId, Title = safeId, Enabled = true };
        m.Id = safeId;
        if (string.IsNullOrEmpty(m.Title)) m.Title = safeId;
        await WriteMetaAsync(safeId, m, ct).ConfigureAwait(false);
        return safeId;
    }

    public Task<bool> DeleteAsync(string taskId, CancellationToken ct = default)
    {
        var safeId = SanitizeId(taskId);
        if (string.IsNullOrEmpty(safeId)) return Task.FromResult(false);
        var mdPath = Path.Combine(_directory, safeId + ".task.md");
        var metaPath = Path.Combine(_directory, safeId + ".meta.json");
        var ok = false;
        if (File.Exists(mdPath))
        {
            File.Delete(mdPath);
            ok = true;
        }
        if (File.Exists(metaPath))
            File.Delete(metaPath);
        return Task.FromResult(ok);
    }

    public async Task UpdateMetaAsync(string taskId, ScheduledTaskMeta meta, CancellationToken ct = default)
    {
        var safeId = SanitizeId(taskId);
        if (string.IsNullOrEmpty(safeId)) return;
        meta.Id = safeId;
        await WriteMetaAsync(safeId, meta, ct).ConfigureAwait(false);
    }

    internal async Task<ScheduledTaskMeta?> ReadMetaAsync(string taskId, CancellationToken ct)
    {
        var path = Path.Combine(_directory, taskId + ".meta.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ScheduledTaskMeta>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteMetaAsync(string taskId, ScheduledTaskMeta meta, CancellationToken ct)
    {
        var path = Path.Combine(_directory, taskId + ".meta.json");
        var json = JsonSerializer.Serialize(meta, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    internal static string SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var s = Regex.Replace(id.Trim(), @"[^\w\-]", "_");
        return s.Length > 0 ? s : "";
    }
}
