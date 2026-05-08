namespace OpenWorkmate.Server.Services;

/// <summary>OCR 服务，供内置 MCP_OCR 工具调用；从图片中提取文本。</summary>
public interface IOcrService
{
    /// <summary>从图片流中提取文字。失败时抛出异常，消息可供用户展示。</summary>
    Task<string> ExtractTextFromImageAsync(Stream imageStream, string? contentType, CancellationToken ct = default);
}
