using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Ocr;

public sealed class OcrExtractorProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OcrExtractorProvider> _logger;

    public OcrExtractorProvider(IHttpClientFactory httpClientFactory, ILogger<OcrExtractorProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> ExtractTextFromImageAsync(Stream imageStream, string? contentType, OcrModelEntry entry, CancellationToken ct = default)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Endpoint) || string.IsNullOrWhiteSpace(entry.ApiKey))
            throw new InvalidOperationException("未配置 OCR。请在「模型设置」中配置 OCR 模型的接口地址与 API Key。");

        var endpoint = entry.Endpoint.Trim().TrimEnd('/');
        var apiKey = entry.ApiKey.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("OCR 接口地址无效。");

        var backend = OcrExtractorResolver.Resolve(entry);

        if (backend == OcrBackendKind.DashScopeOpenAiChatImage)
        {
            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                await imageStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                imageBytes = ms.ToArray();
            }

            var dataUrl = OcrUpstreamAdapter.BuildDataUrlFromImageBytes(imageBytes, contentType ?? "image/png");
            var defaultModelId = "qwen-vl-ocr-latest";
            var prompt = "请只输出图片中的识别文字，不要输出解释或额外格式。";
            var configured = string.IsNullOrWhiteSpace(entry.ModelId) ? null : entry.ModelId.Trim();
            var requestJson = OcrUpstreamAdapter.BuildDashScopeOpenAICompatibleOcrRequestJson(
                defaultModelId,
                dataUrl,
                prompt,
                entry.Language,
                configured);

            var chatUrl = OcrUpstreamAdapter.BuildDashScopeChatCompletionsUrl(endpoint);

            using var http = _httpClientFactory.CreateClient("OCR");
            http.Timeout = TimeSpan.FromMinutes(1);
            var request = new HttpRequestMessage(HttpMethod.Post, chatUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OCR API failed: {Status} {Body}", response.StatusCode,
                    responseText.Length > 300 ? responseText[..300] + "..." : responseText);
                var errMsg = responseText.Length > 200 ? responseText[..200] + "..." : responseText;
                throw new InvalidOperationException("OCR 请求失败: " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? "") +
                                                     (string.IsNullOrEmpty(errMsg) ? "" : " — " + errMsg));
            }

            var extracted = OcrUpstreamAdapter.ExtractOcrTextFromChatCompletionsResponse(responseText);
            return string.IsNullOrWhiteSpace(extracted) ? responseText : extracted;
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(imageStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "image/png");
        content.Add(fileContent, "file", "image");

        using var httpClient = _httpClientFactory.CreateClient("OCR");
        httpClient.Timeout = TimeSpan.FromMinutes(1);
        var request2 = new HttpRequestMessage(HttpMethod.Post, uri);
        request2.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request2.Content = content;

        var response2 = await httpClient.SendAsync(request2, ct).ConfigureAwait(false);
        var responseText2 = await response2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response2.IsSuccessStatusCode)
        {
            _logger.LogWarning("OCR API failed: {Status} {Body}", response2.StatusCode,
                responseText2.Length > 300 ? responseText2[..300] + "..." : responseText2);
            var errMsg = responseText2.Length > 200 ? responseText2[..200] + "..." : responseText2;
            throw new InvalidOperationException("OCR 请求失败: " + (int)response2.StatusCode + " " + (response2.ReasonPhrase ?? "") +
                                                 (string.IsNullOrEmpty(errMsg) ? "" : " — " + errMsg));
        }

        using var doc = JsonDocument.Parse(responseText2);
        var root = doc.RootElement;
        if (root.TryGetProperty("text", out var t))
            return t.GetString() ?? "";
        if (root.TryGetProperty("content", out var c))
            return c.GetString() ?? "";
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString() ?? "";
        return responseText2;
    }
}
