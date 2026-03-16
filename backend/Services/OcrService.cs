using System.Net.Http.Headers;
using System.Text.Json;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

/// <summary>OCR 实现：调用配置的远程 OCR API（兼容 POST 图片、JSON 返回 text/content 的接口）。</summary>
public sealed class OcrService : IOcrService
{
    private readonly ConfigService _configService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OcrService> _logger;

    public OcrService(ConfigService configService, IHttpClientFactory httpClientFactory, ILogger<OcrService> logger)
    {
        _configService = configService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> ExtractTextFromImageAsync(Stream imageStream, string? contentType, CancellationToken ct = default)
    {
        var cfg = _configService.Current;
        var ocr = cfg.Ocr;
        if (ocr == null || string.IsNullOrWhiteSpace(ocr.Endpoint) || string.IsNullOrWhiteSpace(ocr.ApiKey))
            throw new InvalidOperationException("未配置 OCR。请在「模型设置」中配置 OCR 模型的接口地址与 API Key。");

        var endpoint = ocr.Endpoint.Trim().TrimEnd('/');
        var apiKey = ocr.ApiKey.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("OCR 接口地址无效。");

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(imageStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "image/png");
        content.Add(fileContent, "file", "image");

        using var http = _httpClientFactory.CreateClient("OCR");
        http.Timeout = TimeSpan.FromMinutes(1);
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Content = content;

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OCR API failed: {Status} {Body}", response.StatusCode, responseText.Length > 300 ? responseText[..300] + "..." : responseText);
            var errMsg = responseText.Length > 200 ? responseText[..200] + "..." : responseText;
            throw new InvalidOperationException("OCR 请求失败: " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? "") + (string.IsNullOrEmpty(errMsg) ? "" : " — " + errMsg));
        }

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        if (root.TryGetProperty("text", out var t))
            return t.GetString() ?? "";
        if (root.TryGetProperty("content", out var c))
            return c.GetString() ?? "";
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString() ?? "";
        return responseText;
    }
}
