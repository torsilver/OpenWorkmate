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
    public string Store(byte[] data, string extension = ".bin", string? mimeType = null)
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
        _cache[refId] = new CachedAttachment(data, path, DateTime.UtcNow, mimeType);
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
        return Store(bytes, ext, NormalizeMimeType(mimeType));
    }

    private static string? NormalizeMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return null;
        var t = mimeType.Trim();
        return t.Length == 0 ? null : t;
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

    /// <summary>由文件扩展名推断 MIME（供缓存未记录 mime 时多模态注入使用）。</summary>
    public static string? MimeFromFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null
        };
    }

    /// <summary>
    /// 只读取附件字节与 MIME（不驱逐缓存），供多模态注入。成功时 <paramref name="mime"/> 优先用上传时的类型，否则按落盘扩展名推断。
    /// </summary>
    public bool TryGetForVision(string rawRef, out byte[] data, out string? mime)
    {
        data = Array.Empty<byte>();
        mime = null;
        var normalized = AttachmentRefNormalizer.TryNormalize(rawRef);
        if (normalized == null)
            return false;

        if (!_cache.TryGetValue(normalized, out var cached))
            return false;

        if (DateTime.UtcNow - cached.CreatedAt > Ttl)
        {
            _cache.TryRemove(normalized, out _);
            return false;
        }

        data = cached.Data;
        mime = !string.IsNullOrWhiteSpace(cached.MimeType)
            ? cached.MimeType
            : MimeFromFilePath(cached.FilePath);
        return true;
    }

    /// <summary>获取引用对应的本机临时文件路径；若仅内存缓存或已过期则返回 null。</summary>
    public string? GetPath(string attachmentRef)
    {
        if (TryResolvePath(attachmentRef, out var path, out _))
            return path;
        return null;
    }

    /// <summary>
    /// 解析附件引用：规范化键、查缓存、检查 TTL。成功时 <paramref name="path"/> 可能为 null（仅内存缓存且落盘失败时）。
    /// 失败时 <paramref name="failure"/> 有效；成功时其值无意义。
    /// </summary>
    public bool TryResolvePath(string rawInput, out string? path, out AttachmentRefResolveFailure failure)
    {
        path = null;
        failure = default;
        var normalized = AttachmentRefNormalizer.TryNormalize(rawInput);
        if (normalized == null)
        {
            failure = AttachmentRefResolveFailure.InvalidFormat;
            return false;
        }

        if (!_cache.TryGetValue(normalized, out var cached))
        {
            failure = AttachmentRefResolveFailure.NotFound;
            return false;
        }

        if (DateTime.UtcNow - cached.CreatedAt > Ttl)
        {
            _cache.TryRemove(normalized, out _);
            failure = AttachmentRefResolveFailure.Expired;
            return false;
        }

        path = cached.FilePath;
        failure = default;
        return true;
    }

    /// <summary>取回字节并移除缓存（可选）。若仅需路径请用 GetPath。</summary>
    public byte[]? TryTake(string attachmentRef, bool remove = false)
    {
        var normalized = AttachmentRefNormalizer.TryNormalize(attachmentRef);
        if (normalized == null)
            return null;
        var got = remove ? _cache.TryRemove(normalized, out var c) : _cache.TryGetValue(normalized, out c);
        if (!got || c == null)
            return null;
        if (DateTime.UtcNow - c.CreatedAt > Ttl)
        {
            if (!remove) _cache.TryRemove(normalized, out _);
            return null;
        }
        return c.Data;
    }

    private sealed class CachedAttachment
    {
        public byte[] Data { get; }
        public string? FilePath { get; }
        public DateTime CreatedAt { get; }
        public string? MimeType { get; }
        public CachedAttachment(byte[] data, string? filePath, DateTime createdAt, string? mimeType)
        {
            Data = data;
            FilePath = filePath;
            CreatedAt = createdAt;
            MimeType = mimeType;
        }
    }
}
