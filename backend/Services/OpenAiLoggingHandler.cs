using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OfficeCopilot.Server.Logging;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 记录发往 OpenAI 兼容接口的 HTTP 请求体与错误响应体，便于与模型提供方对账与排查。
/// JSON 会以「不转义 Unicode」方式输出，便于直接阅读中文等字符。
/// </summary>
public sealed class OpenAiLoggingHandler : DelegatingHandler
{
    private readonly ILogger<OpenAiLoggingHandler> _logger;
    /// <summary>超过此长度的响应体不做 JSON 规范化解析，避免日志路径占用过大 CPU/内存。</summary>
    private const int MaxJsonNormalizeLength = 256 * 1024;

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public OpenAiLoggingHandler(ILogger<OpenAiLoggingHandler> logger, HttpMessageHandler? inner = null)
    {
        _logger = logger;
        InnerHandler = inner ?? new HttpClientHandler();
    }

    /// <summary>将 JSON 字符串重新序列化为不转义 Unicode 的格式，便于日志阅读；非 JSON 则原样返回。</summary>
    private static string ToReadableJson(string? body)
    {
        if (string.IsNullOrEmpty(body)) return body ?? "";
        if (body.Length > MaxJsonNormalizeLength) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, LogJsonOptions);
        }
        catch
        {
            return body;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBodyLength = 0L;
        if (request.Content != null)
        {
            var contentType = request.Content.Headers.ContentType;
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = contentType ?? new MediaTypeHeaderValue("application/json");
            requestBodyLength = bytes.Length;
        }

        _logger.LogInformation(
            "[AI-HTTP-REQUEST] Method={Method} RequestUri={Uri} RequestBodyLength={Len}",
            request.Method, request.RequestUri, requestBodyLength);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var status = (int)response.StatusCode;
        var errorBodyLen = 0;
        string? errorBodyPreview = null;
        if (response.Content != null && status >= 400)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            response.Content = new ByteArrayContent(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            errorBodyLen = bytes.Length;
            var normalized = ToReadableJson(text);
            // 超大响应体避免整段 SingleLine 扫描
            errorBodyPreview = normalized.Length <= MaxJsonNormalizeLength
                ? LogPreview.HeadTailOmitMiddle(LogPreview.SingleLine(normalized), 400, 400)
                : LogPreview.HeadTailOmitMiddle(normalized, 400, 400);
        }

        if (status >= 400)
            _logger.LogError(
                "[AI-HTTP-RESPONSE] StatusCode={Status} RequestUri={Uri} ResponseBodyLen={Len} ResponseBodyPreview={Preview}",
                status, request.RequestUri, errorBodyLen, errorBodyPreview ?? "");
        else
            _logger.LogInformation("[AI-HTTP-RESPONSE] StatusCode={Status} RequestUri={Uri}",
                status, request.RequestUri);

        return response;
    }
}
