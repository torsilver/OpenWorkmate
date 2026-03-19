using System.Net.Http.Headers;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

/// <summary>语音转文字（Whisper）实现，与 POST /api/transcribe 及内置工具 transcribe_audio 共用配置与逻辑。</summary>
public sealed class TranscribeService : ITranscribeService
{
    private readonly ConfigService _configService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TranscribeService> _logger;

    public TranscribeService(ConfigService configService, IHttpClientFactory httpClientFactory, ILogger<TranscribeService> logger)
    {
        _configService = configService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(Stream audioStream, string? contentType, string? language, CancellationToken ct = default)
    {
        var entry = _configService.GetActiveSttEntry();
        if (entry == null || string.IsNullOrWhiteSpace(entry.Endpoint) || string.IsNullOrWhiteSpace(entry.ApiKey))
            throw new InvalidOperationException("未配置语音转文字。请在设置中配置 STT 模型（Whisper 兼容接口）的 endpoint 与 API Key。");

        var endpoint = entry.Endpoint.Trim().TrimEnd('/');
        var apiKey = entry.ApiKey.Trim();
        var modelId = string.IsNullOrWhiteSpace(entry.ModelId) ? "whisper-1" : entry.ModelId.Trim();

        var url = endpoint.Contains("/v1", StringComparison.OrdinalIgnoreCase) ? endpoint + "/audio/transcriptions" : endpoint + "/v1/audio/transcriptions";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("语音转文字接口地址无效。");

        string? lang = null;
        if (!string.IsNullOrWhiteSpace(language))
            lang = language.Trim();
        else if (entry.Language != null && !string.IsNullOrWhiteSpace(entry.Language))
            lang = entry.Language.Trim();

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(audioStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "audio/mpeg");
        content.Add(fileContent, "file", "audio");
        content.Add(new StringContent(modelId), "model");
        if (!string.IsNullOrEmpty(lang))
            content.Add(new StringContent(lang), "language");

        using var http = _httpClientFactory.CreateClient("STT");
        http.Timeout = TimeSpan.FromMinutes(2);
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Content = content;

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Whisper API failed: {Status} {Body}", response.StatusCode, responseText.Length > 300 ? responseText[..300] + "..." : responseText);
            var errMsg = responseText.Length > 200 ? responseText[..200] + "..." : responseText;
            throw new InvalidOperationException("语音转写请求失败: " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? "") + (string.IsNullOrEmpty(errMsg) ? "" : " — " + errMsg));
        }

        using var doc = System.Text.Json.JsonDocument.Parse(responseText);
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        return text;
    }
}
