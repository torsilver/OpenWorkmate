using System.Collections.Concurrent;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 短时缓存用户附件（图片等），落盘到临时目录，键为 attachment:guid，避免把 base64 传入 AI 上下文。
/// 工具（如 get_attachment_path、OCR MCP）可通过引用在本机按路径读取。
/// </summary>
public sealed class AttachmentCacheService
{
    private readonly ConcurrentDictionary<string, CachedAttachment> _cache = new();
    private readonly ILogger<AttachmentCacheService> _logger;
    private readonly string _tempDir;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public AttachmentCacheService(ILogger<AttachmentCacheService> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData))
            appData = Path.GetTempPath();
        _tempDir = Path.Combine(appData, "OfficeCopilot", "Attachments");
        try
        {
            Directory.CreateDirectory(_tempDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AttachmentCache] Failed to create temp dir {Dir}, will use in-memory only", _tempDir);
        }
    }

    /// <summary>存储附件到临时文件并返回引用。extension 如 ".png"。若落盘失败则仅内存缓存并返回 ref（GetPath 可能不可用）。</summary>
    public string Store(byte[] data, string extension = ".bin")
    {
        var guid = Guid.NewGuid().ToString("N");
        var refId = "attachment:" + guid;
        string? path = null;
        if (!string.IsNullOrEmpty(_tempDir))
        {
            var safeExt = string.IsNullOrEmpty(extension) || extension == "."
                ? ".bin"
                : extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
            path = Path.Combine(_tempDir, guid + safeExt);
            try
            {
                File.WriteAllBytes(path, data);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AttachmentCache] Failed to write file {Path}", path);
                path = null;
            }
        }
        _cache[refId] = new CachedAttachment(data, path, DateTime.UtcNow);
        _logger.LogDebug("[AttachmentCache] Stored ref={Ref} size={Size} path={Path}", refId, data.Length, path ?? "(memory)");
        return refId;
    }

    /// <summary>根据 base64 与 mime 推断扩展名并存储。</summary>
    public string StoreFromBase64(string base64Data, string? mimeType = null)
    {
        if (string.IsNullOrWhiteSpace(base64Data))
            throw new ArgumentException("base64Data is required.", nameof(base64Data));
        var bytes = Convert.FromBase64String(base64Data.Trim());
        var ext = InferExtension(mimeType);
        return Store(bytes, ext);
    }

    private static string InferExtension(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return ".bin";
        if (mimeType.Contains("png", StringComparison.OrdinalIgnoreCase)) return ".png";
        if (mimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || mimeType.Contains("jpg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
        if (mimeType.Contains("gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
        if (mimeType.Contains("webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
        return ".bin";
    }

    /// <summary>获取引用对应的本机临时文件路径；若仅内存缓存或已过期则返回 null。</summary>
    public string? GetPath(string attachmentRef)
    {
        if (string.IsNullOrWhiteSpace(attachmentRef) || !attachmentRef.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!_cache.TryGetValue(attachmentRef, out var cached))
            return null;
        if (DateTime.UtcNow - cached.CreatedAt > Ttl)
        {
            _cache.TryRemove(attachmentRef, out _);
            return null;
        }
        return cached.FilePath;
    }

    /// <summary>取回字节并移除缓存（可选）。若仅需路径请用 GetPath。</summary>
    public byte[]? TryTake(string attachmentRef, bool remove = false)
    {
        if (string.IsNullOrWhiteSpace(attachmentRef) || !attachmentRef.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
            return null;
        var got = remove ? _cache.TryRemove(attachmentRef, out var c) : _cache.TryGetValue(attachmentRef, out c);
        if (!got || c == null)
            return null;
        if (DateTime.UtcNow - c.CreatedAt > Ttl)
        {
            if (!remove) _cache.TryRemove(attachmentRef, out _);
            return null;
        }
        return c.Data;
    }

    private sealed class CachedAttachment
    {
        public byte[] Data { get; }
        public string? FilePath { get; }
        public DateTime CreatedAt { get; }
        public CachedAttachment(byte[] data, string? filePath, DateTime createdAt)
        {
            Data = data;
            FilePath = filePath;
            CreatedAt = createdAt;
        }
    }
}
