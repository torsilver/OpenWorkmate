using OpenWorkmate.Server.Services;

namespace OpenWorkmate.Server.Services.LlmRouting;

/// <summary>
/// LLM 请求经本机 AI Gateway 转发时注入路由头；上游 OpenAI 兼容 base（含 /v1）由 <see cref="X-AI-Upstream-Base"/> 传给 Gateway。
/// </summary>
internal sealed class LlmGatewayHeadersHandler : DelegatingHandler
{
    private readonly string _upstreamBase;

    public LlmGatewayHeadersHandler(string upstreamBase, HttpMessageHandler inner) : base(inner)
    {
        _upstreamBase = (upstreamBase ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(_upstreamBase))
            _upstreamBase = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-AI-Vendor", "dashscope");
        request.Headers.TryAddWithoutValidation("X-AI-Upstream-Base", _upstreamBase);
        var sid = SessionContext.GetSessionId();
        if (!string.IsNullOrEmpty(sid))
            request.Headers.TryAddWithoutValidation("X-AI-Session-Id", sid);
        return base.SendAsync(request, cancellationToken);
    }
}
