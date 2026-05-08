using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services;

namespace OpenWorkmate.Server;

/// <summary>
/// 将带 <c>attachment:</c> 引用的用户轮次组装为 <see cref="ChatMessage"/>；可选注入图片（多模态）。
/// </summary>
internal static class AttachmentRefChatMessageFactory
{
    public const int VisionMaxImagesPerMessage = 8;
    public const int VisionMaxBytesPerImage = 8 * 1024 * 1024;

    public static ChatMessage Build(
        string userMessage,
        IReadOnlyList<string> attachmentRefs,
        bool supportsVision,
        AttachmentCacheService cache,
        ILogger? logger)
    {
        var refList = string.Join(", ", attachmentRefs);
        var messageText = "用户附带了 [" + refList + "]。"
            + (string.IsNullOrWhiteSpace(userMessage) ? "" : " 用户说：" + userMessage);

        if (!supportsVision)
            return new ChatMessage(ChatRole.User, messageText);

        var items = new List<AIContent> { new TextContent(messageText) };
        var added = 0;
        foreach (var rawRef in attachmentRefs)
        {
            if (added >= VisionMaxImagesPerMessage)
                break;

            if (!cache.TryGetForVision(rawRef, out var data, out var mime) || data.Length == 0)
            {
                logger?.LogDebug("[Vision] Skip attachment ref (not in cache or empty): {Ref}", rawRef);
                continue;
            }

            if (data.Length > VisionMaxBytesPerImage)
            {
                logger?.LogDebug("[Vision] Skip attachment ref (too large): {Ref} bytes={Len}", rawRef, data.Length);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mime))
            {
                logger?.LogDebug("[Vision] Skip attachment ref (unknown MIME): {Ref}", rawRef);
                continue;
            }

            items.Add(new DataContent(data, mime.Trim()));
            added++;
        }

        return new ChatMessage(ChatRole.User, items);
    }
}
