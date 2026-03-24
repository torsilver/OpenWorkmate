using System.IO;
using System.Net.Http.Headers;
using System.Text;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Stt;

/// <summary>按解析后的上游类型执行语音转写。</summary>
public sealed class SttTranscriberProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SttTranscriberProvider> _logger;

    public SttTranscriberProvider(IHttpClientFactory httpClientFactory, ILogger<SttTranscriberProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(Stream audioStream, string? contentType, string? language, SttModelEntry entry, CancellationToken ct = default)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Endpoint) || string.IsNullOrWhiteSpace(entry.ApiKey))
            throw new InvalidOperationException("未配置语音转文字。请在设置中配置 STT 模型的 endpoint 与 API Key。");

        var endpoint = SttUpstreamAdapter.NormalizeEndpoint(entry.Endpoint);
        var apiKey = entry.ApiKey.Trim();
        var modelId = string.IsNullOrWhiteSpace(entry.ModelId) ? "whisper-1" : entry.ModelId.Trim();

        string? lang = null;
        if (!string.IsNullOrWhiteSpace(language))
            lang = language.Trim();
        else if (entry.Language != null && !string.IsNullOrWhiteSpace(entry.Language))
            lang = entry.Language.Trim();

        var kind = SttTranscriberResolver.Resolve(entry);

        if (kind == SttUpstreamAdapter.UpstreamKind.DashScopeQwenOpenAICompatible)
        {
            SttUpstreamAdapter.ValidateDashScopeModelIdPresent(modelId);
            var dashScopeContentType = string.IsNullOrWhiteSpace(contentType) ? "audio/mpeg" : contentType.Trim();

            const int dashScopeMaxBytes = 10 * 1024 * 1024;
            byte[] audioBytes;
            using (var ms = new MemoryStream())
            {
                await audioStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                audioBytes = ms.ToArray();
            }

            if (audioBytes.Length > dashScopeMaxBytes)
                throw new InvalidOperationException("DashScope 兼容模式单文件超过 10MB 限制，请使用更短的音频或切换到 Whisper。");

            var dataUrl = SttUpstreamAdapter.BuildAudioDataUrl(audioBytes, dashScopeContentType);
            var url = SttUpstreamAdapter.BuildDashScopeChatCompletionsUrl(endpoint);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException("语音转文字接口地址无效。");

            var jsonPayload = SttUpstreamAdapter.BuildDashScopeOpenAICompatibleRequestJson(modelId, dataUrl, lang);
            using var http = _httpClientFactory.CreateClient("STT");
            http.Timeout = TimeSpan.FromMinutes(2);
            var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DashScope STT API failed: {Status} {Body}", response.StatusCode, responseText.Length > 300 ? responseText[..300] + "..." : responseText);
                var errMsg = responseText.Length > 200 ? responseText[..200] + "..." : responseText;
                throw new InvalidOperationException("语音转写请求失败: " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? "") + (string.IsNullOrEmpty(errMsg) ? "" : " — " + errMsg));
            }

            return SttUpstreamAdapter.ExtractTranscriptFromDashScopeResponse(responseText);
        }

        var urlWhisper = SttUpstreamAdapter.BuildWhisperTranscriptionsUrl(endpoint);
        if (!Uri.TryCreate(urlWhisper, UriKind.Absolute, out var whisperUri) || (whisperUri.Scheme != "http" && whisperUri.Scheme != "https"))
            throw new InvalidOperationException("语音转文字接口地址无效。");

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(audioStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "audio/mpeg");
        content.Add(fileContent, "file", "audio");
        content.Add(new StringContent(modelId), "model");
        if (!string.IsNullOrEmpty(lang))
            content.Add(new StringContent(lang), "language");

        using var http2 = _httpClientFactory.CreateClient("STT");
        http2.Timeout = TimeSpan.FromMinutes(2);
        var requestWhisper = new HttpRequestMessage(HttpMethod.Post, whisperUri);
        requestWhisper.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        requestWhisper.Content = content;

        var responseWhisper = await http2.SendAsync(requestWhisper, ct).ConfigureAwait(false);
        var responseTextWhisper = await responseWhisper.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!responseWhisper.IsSuccessStatusCode)
        {
            _logger.LogWarning("Whisper API failed: {Status} {Body}", responseWhisper.StatusCode, responseTextWhisper.Length > 300 ? responseTextWhisper[..300] + "..." : responseTextWhisper);
            var errMsg = responseTextWhisper.Length > 200 ? responseTextWhisper[..200] + "..." : responseTextWhisper;
            throw new InvalidOperationException("语音转写请求失败: " + (int)responseWhisper.StatusCode + " " + (responseWhisper.ReasonPhrase ?? "") + (string.IsNullOrEmpty(errMsg) ? "" : " — " + errMsg));
        }

        using var doc = System.Text.Json.JsonDocument.Parse(responseTextWhisper);
        var whisperText = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        return whisperText;
    }
}
