using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services.Plan;

/// <summary>基于文件系统的计划存储：每份计划一个 .plan.md 文件 + 同名的 .meta.json。</summary>
public sealed class FilePlanStore : IPlanStore
{
    private readonly string _directory;
    private readonly ILogger<FilePlanStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public FilePlanStore(string directory, ILogger<FilePlanStore> logger)
    {
        _directory = Path.GetFullPath(directory);
        _logger = logger;
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plans directory create failed: {Dir}", _directory);
        }
    }

    public async Task<IReadOnlyList<PlanMeta>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<PlanMeta>();
        if (!Directory.Exists(_directory)) return list;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.plan.md"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file)); // x.plan -> x
            var meta = await ReadMetaAsync(name, ct).ConfigureAwait(false);
            list.Add(meta ?? new PlanMeta
            {
                Id = name,
                Title = name,
                Status = "draft",
                CreatedAt = DateTimeOffset.FromFileTime(0),
                UpdatedAt = DateTimeOffset.FromFileTime(0)
            });
        }
        return list.OrderByDescending(m => m.UpdatedAt).ToList();
    }

    public async Task<(string Content, PlanMeta Meta)?> GetAsync(string planId, CancellationToken ct = default)
    {
        var safeId = SanitizeId(planId);
        if (string.IsNullOrEmpty(safeId)) return null;
        var path = Path.Combine(_directory, safeId + ".plan.md");
        if (!File.Exists(path)) return null;
        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var meta = await ReadMetaAsync(safeId, ct).ConfigureAwait(false)
            ?? new PlanMeta { Id = safeId, Title = safeId, Status = "draft", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        return (content, meta);
    }

    public async Task<string> SaveAsync(string planId, string content, PlanMeta? meta = null, CancellationToken ct = default)
    {
        var safeId = SanitizeId(planId);
        if (string.IsNullOrEmpty(safeId))
            safeId = Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(_directory, safeId + ".plan.md");
        await File.WriteAllTextAsync(path, content ?? "", ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var m = meta ?? new PlanMeta { Id = safeId, Title = safeId, Status = "draft", CreatedAt = now, UpdatedAt = now };
        m.Id = safeId;
        m.UpdatedAt = now;
        if (m.CreatedAt == default) m.CreatedAt = now;
        if (string.IsNullOrEmpty(m.Title)) m.Title = safeId;
        await WriteMetaAsync(safeId, m, ct).ConfigureAwait(false);
        return safeId;
    }

    public Task<bool> DeleteAsync(string planId, CancellationToken ct = default)
    {
        var safeId = SanitizeId(planId);
        if (string.IsNullOrEmpty(safeId)) return Task.FromResult(false);
        var mdPath = Path.Combine(_directory, safeId + ".plan.md");
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

    private async Task<PlanMeta?> ReadMetaAsync(string planId, CancellationToken ct)
    {
        var path = Path.Combine(_directory, planId + ".meta.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PlanMeta>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteMetaAsync(string planId, PlanMeta meta, CancellationToken ct)
    {
        var path = Path.Combine(_directory, planId + ".meta.json");
        var json = JsonSerializer.Serialize(meta, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    private static string SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var s = Regex.Replace(id.Trim(), @"[^\w\-]", "_");
        return s.Length > 0 ? s : "";
    }
}
