namespace OpenWorkmate.Server.Services;

/// <summary><see cref="AttachmentCacheService.TryResolvePath"/> 失败原因，供工具返回明确文案与日志。</summary>
public enum AttachmentRefResolveFailure
{
    InvalidFormat,
    NotFound,
    Expired
}
