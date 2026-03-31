using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AttachmentCacheServiceResolveTests
{
    [Fact]
    public void TryResolvePath_BareGuid_WhenStored_Resolves()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);
        var refFull = cache.Store(new byte[] { 1, 2, 3 }, ".png");
        Assert.StartsWith("attachment:", refFull, StringComparison.Ordinal);
        var bare = refFull["attachment:".Length..];

        Assert.True(cache.TryResolvePath(bare, out var path, out var failure));
        Assert.Equal(default(AttachmentRefResolveFailure), failure);
        Assert.False(string.IsNullOrEmpty(path));
    }

    [Fact]
    public void TryResolvePath_UnknownGuid_ReturnsNotFound()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);
        var randomHex = Guid.NewGuid().ToString("N");

        Assert.False(cache.TryResolvePath(randomHex, out var path, out var failure));
        Assert.Null(path);
        Assert.Equal(AttachmentRefResolveFailure.NotFound, failure);
    }

    [Fact]
    public void TryResolvePath_InvalidFormat_ReturnsInvalidFormat()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);

        Assert.False(cache.TryResolvePath("not-a-valid-ref", out var path, out var failure));
        Assert.Null(path);
        Assert.Equal(AttachmentRefResolveFailure.InvalidFormat, failure);
    }
}
