using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.Ocr;

namespace OfficeCopilot.Server.Services;

/// <summary>OCR：委托 <see cref="OcrExtractorProvider"/>。</summary>
public sealed class OcrService : IOcrService
{
    private readonly ConfigService _configService;
    private readonly OcrExtractorProvider _ocrExtractorProvider;

    public OcrService(ConfigService configService, OcrExtractorProvider ocrExtractorProvider)
    {
        _configService = configService;
        _ocrExtractorProvider = ocrExtractorProvider;
    }

    public Task<string> ExtractTextFromImageAsync(Stream imageStream, string? contentType, CancellationToken ct = default)
    {
        var entry = _configService.GetActiveOcrEntry();
        if (entry == null || string.IsNullOrWhiteSpace(entry.Endpoint) || string.IsNullOrWhiteSpace(entry.ApiKey))
            throw new InvalidOperationException("未配置 OCR。请在「模型设置」中配置 OCR 模型的接口地址与 API Key。");
        return _ocrExtractorProvider.ExtractTextFromImageAsync(imageStream, contentType, entry, ct);
    }
}
