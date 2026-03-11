using System.Collections.Concurrent;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 短时缓存整页截图（PNG 字节），键为 screenshot:guid，避免把 base64 传入 AI 上下文。
/// </summary>
public sealed class ScreenshotCacheService
{
    private readonly ConcurrentDictionary<string, CachedScreenshot> _cache = new();
    private readonly ILogger<ScreenshotCacheService> _logger;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public ScreenshotCacheService(ILogger<ScreenshotCacheService> logger) => _logger = logger;

    public string Store(byte[] pngBytes)
    {
        var refId = "screenshot:" + Guid.NewGuid().ToString("N");
        _cache[refId] = new CachedScreenshot(pngBytes, DateTime.UtcNow);
        _logger.LogDebug("[ScreenshotCache] Stored ref={Ref} size={Size}", refId, pngBytes.Length);
        return refId;
    }

    public byte[]? TryTake(string screenshotRef)
    {
        if (string.IsNullOrWhiteSpace(screenshotRef) || !screenshotRef.StartsWith("screenshot:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (_cache.TryRemove(screenshotRef, out var cached))
        {
            if (DateTime.UtcNow - cached.CreatedAt > Ttl)
            {
                _logger.LogDebug("[ScreenshotCache] Ref expired ref={Ref}", screenshotRef);
                return null;
            }
            _logger.LogDebug("[ScreenshotCache] Taken ref={Ref}", screenshotRef);
            return cached.Data;
        }
        return null;
    }

    private sealed class CachedScreenshot
    {
        public byte[] Data { get; }
        public DateTime CreatedAt { get; }
        public CachedScreenshot(byte[] data, DateTime createdAt) { Data = data; CreatedAt = createdAt; }
    }
}
