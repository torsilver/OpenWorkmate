using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 准确数据插件：以文件形式持久化与按 id 读取规范数据，供 AI 写入与精确检索。
/// 目录由 ConfigService.AccurateDataDirectory 配置，为空时使用 %LocalAppData%/OfficeCopilot/AccurateData。
/// </summary>
public sealed class AccurateDataPlugin
{
    private readonly ConfigService _configService;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public AccurateDataPlugin(ConfigService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    private string GetRootDirectory()
    {
        var dir = (_configService.Current.AccurateDataDirectory ?? "").Trim();
        if (string.IsNullOrEmpty(dir))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dir = Path.Combine(appData, "OfficeCopilot", "AccurateData");
        }
        else
        {
            dir = Environment.ExpandEnvironmentVariables(dir);
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(AppContext.BaseDirectory, dir);
        }
        dir = Path.GetFullPath(dir);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // ignore; will fail on write
        }
        return dir;
    }

    private static string SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var s = Regex.Replace(id.Trim(), @"[^\w\-]", "_");
        return s.Length > 0 ? s : "";
    }

    private string GetFilePath(string safeId, string format)
    {
        var ext = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? ".json" : ".md";
        return Path.Combine(GetRootDirectory(), safeId + ext);
    }

    private void EnsurePathInRoot(string fullPath)
    {
        var root = GetRootDirectory();
        var normalized = Path.GetFullPath(fullPath);
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path must be within the accurate data directory.");
    }

    [KernelFunction("accurate_data_write")]
    [Description("Write or overwrite one accurate data entry. Use when you need to persist data for later exact retrieval. id: unique key (alphanumeric, dash, underscore). format: 'md' or 'json' (default md).")]
    public async Task<string> AccurateDataWriteAsync(
        [Description("Unique identifier for this entry (e.g. task_summary, report_20240314)")] string id,
        [Description("Content to store (Markdown or JSON text)")] string content,
        [Description("Format: 'md' or 'json'")] string format = "md",
        CancellationToken cancellationToken = default)
    {
        var safeId = SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            return "[Error] id is required and must contain only letters, digits, underscore, or hyphen.";
        var path = GetFilePath(safeId, format);
        EnsurePathInRoot(path);
        await File.WriteAllTextAsync(path, content ?? "", cancellationToken).ConfigureAwait(false);
        var metaPath = Path.Combine(GetRootDirectory(), safeId + ".meta.json");
        var meta = new { updatedAt = DateTime.UtcNow.ToString("O") };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOptions), cancellationToken).ConfigureAwait(false);
        return $"[OK] Saved accurate data: id={safeId}, format={format}.";
    }

    [KernelFunction("accurate_data_read")]
    [Description("Read one accurate data entry by id. Returns the raw content. Use when you need to use previously stored data.")]
    public async Task<string> AccurateDataReadAsync(
        [Description("Id of the entry (as used in accurate_data_write)")] string id,
        CancellationToken cancellationToken = default)
    {
        var safeId = SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            return "[Error] id is required.";
        var root = GetRootDirectory();
        var mdPath = Path.Combine(root, safeId + ".md");
        var jsonPath = Path.Combine(root, safeId + ".json");
        string? path = null;
        if (File.Exists(mdPath)) path = mdPath;
        else if (File.Exists(jsonPath)) path = jsonPath;
        if (path == null)
            return $"[Not found] No entry for id={safeId}.";
        EnsurePathInRoot(path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return text;
    }

    [KernelFunction("accurate_data_list")]
    [Description("List accurate data entries. Optionally filter by id prefix and limit count. Returns id and format for each entry.")]
    public Task<string> AccurateDataListAsync(
        [Description("Optional prefix to filter ids (e.g. 'task_')")] string? prefix = null,
        [Description("Max number of entries to return")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var root = GetRootDirectory();
        if (!Directory.Exists(root))
            return Task.FromResult("(no entries)");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*.*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            var ext = Path.GetExtension(file);
            if (ext == ".meta.json") continue;
            if (ext != ".md" && ext != ".json") continue;
            if (!string.IsNullOrWhiteSpace(prefix) && !name.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            ids.Add(name);
        }
        var list = ids.OrderBy(x => x).Take(Math.Clamp(limit, 1, 200)).ToList();
        if (list.Count == 0)
            return Task.FromResult("(no entries)");
        var lines = list.Select(i =>
        {
            var hasMd = File.Exists(Path.Combine(root, i + ".md"));
            var fmt = hasMd ? "md" : "json";
            return $"- {i} ({fmt})";
        });
        return Task.FromResult(string.Join("\n", lines));
    }

    [KernelFunction("accurate_data_delete")]
    [Description("Delete one accurate data entry by id (removes both content and meta).")]
    public Task<string> AccurateDataDeleteAsync(
        [Description("Id of the entry to delete")] string id,
        CancellationToken cancellationToken = default)
    {
        var safeId = SanitizeId(id);
        if (string.IsNullOrEmpty(safeId))
            return Task.FromResult("[Error] id is required.");
        var root = GetRootDirectory();
        var mdPath = Path.Combine(root, safeId + ".md");
        var jsonPath = Path.Combine(root, safeId + ".json");
        var metaPath = Path.Combine(root, safeId + ".meta.json");
        var deleted = false;
        if (File.Exists(mdPath)) { EnsurePathInRoot(mdPath); File.Delete(mdPath); deleted = true; }
        if (File.Exists(jsonPath)) { EnsurePathInRoot(jsonPath); File.Delete(jsonPath); deleted = true; }
        if (File.Exists(metaPath)) { EnsurePathInRoot(metaPath); File.Delete(metaPath); }
        return Task.FromResult(deleted ? $"[OK] Deleted id={safeId}." : $"[Not found] No entry for id={safeId}.");
    }
}
