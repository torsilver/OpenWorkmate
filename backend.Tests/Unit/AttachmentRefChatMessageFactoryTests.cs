using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AttachmentRefChatMessageFactoryTests
{
    [Fact]
    public void Build_WithoutVision_UsesPlainTextUserMessage()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);
        var refId = cache.StoreFromBase64(
            Convert.ToBase64String(new byte[] { 137, 80, 78, 71 }),
            "image/png");

        var msg = AttachmentRefChatMessageFactory.Build(
            "hello",
            new[] { refId },
            supportsVision: false,
            cache,
            NullLogger.Instance);

        Assert.Equal(AuthorRole.User, msg.Role);
        Assert.Contains("attachment:", msg.Content ?? "", StringComparison.Ordinal);
        Assert.Contains("hello", msg.Content ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(msg.Items ?? [], i => i is ImageContent);
    }

    [Fact]
    public void Build_WithVision_AddsImageContentItems()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);
        var refId = cache.StoreFromBase64(
            Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/png");

        var msg = AttachmentRefChatMessageFactory.Build(
            "describe",
            new[] { refId },
            supportsVision: true,
            cache,
            NullLogger.Instance);

        Assert.Equal(AuthorRole.User, msg.Role);
        Assert.NotNull(msg.Items);
        Assert.True(msg.Items.Count >= 2);
        Assert.Contains(msg.Items, i => i is TextContent);
        Assert.Contains(msg.Items, i => i is ImageContent);
    }

    [Fact]
    public void Build_WithVision_UnknownMime_SkipsImage()
    {
        var cache = new AttachmentCacheService(NullLogger<AttachmentCacheService>.Instance);
        var refId = cache.Store(new byte[] { 1, 2, 3 }, ".bin", mimeType: null);

        var msg = AttachmentRefChatMessageFactory.Build(
            "x",
            new[] { refId },
            supportsVision: true,
            cache,
            NullLogger.Instance);

        Assert.NotNull(msg.Items);
        Assert.DoesNotContain(msg.Items, i => i is ImageContent);
        Assert.Contains(msg.Items, i => i is TextContent);
    }
}
