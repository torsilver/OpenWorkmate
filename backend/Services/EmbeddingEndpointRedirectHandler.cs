using System.Net;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// 将发往 api.openai.com 的请求重写到指定的 OpenAI 兼容 endpoint（如阿里 dashscope）。
/// SK 的 AddOpenAITextEmbeddingGeneration(modelId, apiKey, orgId?, serviceId, httpClient) 无 endpoint 参数，客户端会请求 api.openai.com；通过本 Handler 在发出前改写 RequestUri。
/// </summary>
public sealed class EmbeddingEndpointRedirectHandler : DelegatingHandler
{
    private readonly Uri _embeddingBaseUri;

    public EmbeddingEndpointRedirectHandler(string embeddingEndpointBase)
    {
        if (string.IsNullOrWhiteSpace(embeddingEndpointBase) || !Uri.TryCreate(embeddingEndpointBase.Trim().TrimEnd('/'), UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Invalid embedding endpoint base.", nameof(embeddingEndpointBase));
        }
        _embeddingBaseUri = u;
        InnerHandler = new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri != null
            && string.Equals(request.RequestUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            // 路径如 /v1/embeddings，拼到配置的 base 后；不能用 new Uri(base, "/embeddings) 会覆盖 base 的 path 导致丢失 /compatible-mode/v1
            var path = request.RequestUri.AbsolutePath;
            if (path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
                path = path.Length == 3 ? "/" : path[3..];
            var query = string.IsNullOrEmpty(request.RequestUri.Query) ? "" : request.RequestUri.Query;
            var baseStr = _embeddingBaseUri.ToString().TrimEnd('/');
            var pathTrimmed = path.TrimStart('/');
            request.RequestUri = new Uri(string.IsNullOrEmpty(pathTrimmed) ? baseStr + query : baseStr + "/" + pathTrimmed + query);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
