using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>内置 OCR 能力，以 MCP 风格插件名 MCP_OCR 注册，供主模型按需调用 ocr_image。</summary>
public sealed class OcrPlugin
{
    private readonly IOcrService _ocrService;
    private readonly ILogger<OcrPlugin> _logger;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif" };
    private const long MaxImageSize = 20 * 1024 * 1024; // 20 MB

    public OcrPlugin(IOcrService ocrService, ILogger<OcrPlugin> logger)
    {
        _ocrService = ocrService;
        _logger = logger;
    }

    [KernelFunction("ocr_image")]
    [Description("Extract text from an image file at the given local path. Use when the user asks to get text from an image or to turn images into a document. Path must be accessible from this machine (e.g. from get_attachment_path). Returns the recognized text or an error message.")]
    public async Task<string> OcrImageAsync(
        [Description("Full local path to the image file (e.g. C:\\temp\\screenshot.png or path from get_attachment_path)")] string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "失败：请提供图片文件路径。";
        var path = filePath.Trim();
        if (!File.Exists(path))
        {
            _logger.LogWarning("[MCP_OCR] ocr_image file not found: {Path}", path);
            return "失败：文件不存在或路径不可访问（" + path + "）。";
        }
        var fi = new FileInfo(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!ImageExtensions.Contains(ext))
            return "失败：仅支持图片格式（png、jpg、jpeg、gif、bmp、webp、tiff）。";
        if (fi.Length > MaxImageSize)
            return "失败：图片超过 20MB 限制。";
        if (fi.Length == 0)
            return "失败：文件为空。";

        string? contentType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".tiff" or ".tif" => "image/tiff",
            _ => "image/png"
        };

        try
        {
            await using var stream = File.OpenRead(path);
            var text = await _ocrService.ExtractTextFromImageAsync(stream, contentType, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(text) ? "（未识别到文字）" : text;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("[MCP_OCR] ocr_image: {Message}", ex.Message);
            return "失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MCP_OCR] ocr_image failed for {Path}", path);
            return "失败：OCR 出错（" + ex.Message + "）。";
        }
    }
}
