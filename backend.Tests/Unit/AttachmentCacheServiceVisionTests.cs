using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AttachmentCacheServiceVisionTests
{
    [Fact]
    public void TryGetForVision_AfterStoreFromBase64_ReturnsBytesAndMime()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);
        var png1x1 = Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var refId = cache.StoreFromBase64(png1x1, "image/png");

        Assert.True(cache.TryGetForVision(refId, out var data, out var mime));
        Assert.Equal("image/png", mime);
        Assert.Equal(8, data.Length);
    }

    [Fact]
    public void MimeFromFilePath_MapsCommonExtensions()
    {
        Assert.Equal("image/png", AttachmentCacheService.MimeFromFilePath(@"C:\x\a.PNG"));
        Assert.Equal("image/jpeg", AttachmentCacheService.MimeFromFilePath("b.jpg"));
        Assert.Equal("image/webp", AttachmentCacheService.MimeFromFilePath("c.webp"));
        Assert.Null(AttachmentCacheService.MimeFromFilePath("d.bin"));
    }

    [Fact]
    public void TryGetForVision_UsesPathWhenMimeNotStored()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);
        var refId = cache.Store(new byte[] { 1, 2, 3 }, ".png", mimeType: null);
        Assert.True(cache.TryGetForVision(refId, out _, out var mime));
        Assert.Equal("image/png", mime);
    }
}
