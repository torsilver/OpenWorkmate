using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 记录发往 OpenAI 兼容接口的 HTTP 请求体与错误响应体，便于与模型提供方对账与排查。
/// JSON 会以「不转义 Unicode」方式输出，便于直接阅读中文等字符。
/// </summary>
public sealed class OpenAiLoggingHandler : DelegatingHandler
{
    private readonly ILogger<OpenAiLoggingHandler> _logger;
    private const int MaxBodyLogLength = 32 * 1024; // 32KB

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public OpenAiLoggingHandler(ILogger<OpenAiLoggingHandler> logger)
    {
        _logger = logger;
        InnerHandler = new HttpClientHandler();
    }

    /// <summary>将 JSON 字符串重新序列化为不转义 Unicode 的格式，便于日志阅读；非 JSON 则原样返回。</summary>
    private static string ToReadableJson(string? body)
    {
        if (string.IsNullOrEmpty(body)) return body ?? "";
        if (body.Length > MaxBodyLogLength) return body;
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
        var reqBody = "";
        if (request.Content != null)
        {
            var contentType = request.Content.Headers.ContentType;
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = contentType ?? new MediaTypeHeaderValue("application/json");
            reqBody = Encoding.UTF8.GetString(bytes);
            if (reqBody.Length > MaxBodyLogLength)
                reqBody = reqBody.AsSpan(0, MaxBodyLogLength).ToString() + $"... [truncated, total {bytes.Length} bytes]";
            else
                reqBody = ToReadableJson(reqBody);
        }

        _logger.LogInformation(
            "[AI-HTTP-REQUEST] Method={Method} RequestUri={Uri} RequestBodyLength={Len} RequestBody={Body}",
            request.Method, request.RequestUri, reqBody.Length, reqBody);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var status = (int)response.StatusCode;
        var resBody = "";
        if (response.Content != null && status >= 400)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            response.Content = new ByteArrayContent(bytes);
            resBody = Encoding.UTF8.GetString(bytes);
            if (resBody.Length > MaxBodyLogLength)
                resBody = resBody.AsSpan(0, MaxBodyLogLength).ToString() + $"... [truncated, total {bytes.Length} bytes]";
            else
                resBody = ToReadableJson(resBody);
        }

        if (status >= 400)
            _logger.LogError(
                "[AI-HTTP-RESPONSE] StatusCode={Status} RequestUri={Uri} ResponseBody={Body}",
                status, request.RequestUri, resBody);
        else
            _logger.LogInformation("[AI-HTTP-RESPONSE] StatusCode={Status} RequestUri={Uri}",
                status, request.RequestUri);

        return response;
    }
}
