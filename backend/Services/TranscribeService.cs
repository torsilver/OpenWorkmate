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
        var cfg = _configService.Current;
        string endpoint, apiKey;
        if (cfg.SpeechToText != null && !string.IsNullOrWhiteSpace(cfg.SpeechToText.Endpoint) && !string.IsNullOrWhiteSpace(cfg.SpeechToText.ApiKey))
        {
            endpoint = cfg.SpeechToText.Endpoint.Trim().TrimEnd('/');
            apiKey = cfg.SpeechToText.ApiKey.Trim();
        }
        else if (cfg.AiModels != null && cfg.AiModels.Count > 0)
        {
            var active = cfg.AiModels.FirstOrDefault(m => string.Equals(m.Id, cfg.ActiveModelId, StringComparison.OrdinalIgnoreCase)) ?? cfg.AiModels[0];
            endpoint = (active.Endpoint ?? "").Trim().TrimEnd('/');
            apiKey = (active.ApiKey ?? "").Trim();
        }
        else
        {
            endpoint = (cfg.AI?.Endpoint ?? "https://api.openai.com/v1").Trim().TrimEnd('/');
            apiKey = (cfg.AI?.ApiKey ?? "").Trim();
        }

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("未配置语音转文字 API 密钥。请在设置中配置「语音转文字」或使用支持 Whisper 的 AI 模型 endpoint/apiKey。");

        var url = endpoint.Contains("/v1", StringComparison.OrdinalIgnoreCase) ? endpoint + "/audio/transcriptions" : endpoint + "/v1/audio/transcriptions";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("语音转文字接口地址无效。");

        string? lang = null;
        if (!string.IsNullOrWhiteSpace(language))
            lang = language.Trim();
        else if (cfg.SpeechToText?.Language != null && !string.IsNullOrWhiteSpace(cfg.SpeechToText.Language))
            lang = cfg.SpeechToText.Language.Trim();

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(audioStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "audio/mpeg");
        content.Add(fileContent, "file", "audio");
        content.Add(new StringContent("whisper-1"), "model");
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
