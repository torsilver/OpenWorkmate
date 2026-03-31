using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server;

/// <summary>
/// 将带 <c>attachment:</c> 引用的用户轮次组装为 <see cref="ChatMessageContent"/>；可选注入 <see cref="ImageContent"/>（多模态）。
/// </summary>
internal static class AttachmentRefChatMessageFactory
{
    public const int VisionMaxImagesPerMessage = 8;
    public const int VisionMaxBytesPerImage = 8 * 1024 * 1024;

    public static ChatMessageContent Build(
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
            return new ChatMessageContent(AuthorRole.User, messageText);

        var items = new ChatMessageContentItemCollection { new TextContent(messageText) };
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

            var b64 = Convert.ToBase64String(data);
            var dataUri = "data:" + mime.Trim() + ";base64," + b64;
            items.Add(new ImageContent(dataUri));
            added++;
        }

        return new ChatMessageContent(AuthorRole.User, items);
    }
}
