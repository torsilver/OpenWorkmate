using System.Collections.Concurrent;
using System.Net.Http.Headers;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.Chat;
using OfficeCopilot.Server.Services.DashScope;

namespace OfficeCopilot.Server.Services.OpenAiCompat;

/// <summary>
/// 非百炼 OpenAI 兼容 <c>chat/completions</c> 流式响应：不改写请求体，仅挂 SSE 旁路解析
/// <c>reasoning_content</c> 与顶层 <c>usage</c>（与 <see cref="DashScopeOpenAiCompatHandler"/> 共享解析与队列模型）。
/// </summary>
internal sealed class OpenAiReasoningSseTapDelegatingHandler : DelegatingHandler
{
    private readonly string _modelEntryId;
    private readonly ILogger<OpenAiReasoningSseTapDelegatingHandler>? _logger;
    private readonly TimelineBlockStreamCoordinator? _timelineBlockCoordinator;

    public OpenAiReasoningSseTapDelegatingHandler(
        string modelEntryId,
        HttpMessageHandler inner,
        ILogger<OpenAiReasoningSseTapDelegatingHandler>? logger = null,
        TimelineBlockStreamCoordinator? timelineBlockCoordinator = null)
        : base(inner)
    {
        _modelEntryId = modelEntryId ?? "";
        _logger = logger;
        _timelineBlockCoordinator = timelineBlockCoordinator;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post
            || request.RequestUri is null
            || !request.RequestUri.IsAbsoluteUri
            || !request.RequestUri.AbsolutePath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        byte[]? bodyBytes = null;
        if (request.Content != null)
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var isStream = bodyBytes != null && DashScopeChatRequestMerge.RequestBodyIndicatesStream(bodyBytes);
        if (!isStream)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var reasoningQueue = DashScopeReasoningContext.PushFrame();
        var bridgeSessionId = SessionContext.GetSessionId();
        DashScopeReasoningSessionBridge.AttachQueue(bridgeSessionId, reasoningQueue);
        var usageQueue = new ConcurrentQueue<string>();
        OpenAiStreamUsageSessionBridge.AttachQueue(bridgeSessionId, usageQueue);
        if (!DashScopeCallKindContext.IsBackground
            && !string.IsNullOrEmpty(bridgeSessionId)
            && _timelineBlockCoordinator != null)
            _timelineBlockCoordinator.OnMainChatReasoningSourceAttached(bridgeSessionId);

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var contentTypeHdr = response.Content.Headers.ContentType?.ToString() ?? "(null)";
            var isEvent = IsEventStream(response);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug(
                    "[OpenAiSseTap] entry={Entry} status={Status} contentType={Ct} (no tap)",
                    _modelEntryId, (int)response.StatusCode, contentTypeHdr);
                DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                OpenAiStreamUsageSessionBridge.TryDetachQueue(bridgeSessionId, usageQueue);
                DashScopeReasoningContext.PopFrame();
                return response;
            }

            if (!isEvent)
            {
                DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                OpenAiStreamUsageSessionBridge.TryDetachQueue(bridgeSessionId, usageQueue);
                DashScopeReasoningContext.PopFrame();
                return response;
            }

            _logger?.LogDebug(
                "[OpenAiSseTap] entry={Entry} contentType={Ct} attaching SSE reasoning+usage tap",
                _modelEntryId, contentTypeHdr);

            try
            {
                var mediaType = response.Content.Headers.ContentType;
                var innerStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var telemetry = new DashScopeSseTapTelemetry();
                var tap = new DashScopeSseReasoningTapStream(
                    innerStream,
                    fragment =>
                    {
                        if (string.IsNullOrEmpty(fragment))
                            return;
                        reasoningQueue.Enqueue(fragment);
                    },
                    telemetry,
                    onUsageJson: usageJson =>
                    {
                        if (!string.IsNullOrEmpty(usageJson))
                            usageQueue.Enqueue(usageJson);
                    });
                var wrapped = new PopFrameOnDisposeStream(tap, () =>
                {
                    DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                    OpenAiStreamUsageSessionBridge.TryDetachQueue(bridgeSessionId, usageQueue);
                    _logger?.LogDebug(
                        "[OpenAiSseTap] closed entry={Entry}: sseDataLines={DataLines} reasoningParsed={Parsed} jsonErrors={JsonErr}",
                        _modelEntryId,
                        telemetry.SseDataLines,
                        telemetry.ReasoningFragmentsParsed,
                        telemetry.JsonParseErrors);
                    DashScopeReasoningContext.PopFrame();
                });
                var newBody = new StreamContent(wrapped);
                if (mediaType != null)
                    newBody.Headers.ContentType = mediaType;
                response.Content = newBody;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "OpenAi SSE tap wrap failed for model entry {EntryId}", _modelEntryId);
                DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                OpenAiStreamUsageSessionBridge.TryDetachQueue(bridgeSessionId, usageQueue);
                DashScopeReasoningContext.PopFrame();
                throw;
            }

            return response;
        }
        catch
        {
            DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
            OpenAiStreamUsageSessionBridge.TryDetachQueue(bridgeSessionId, usageQueue);
            DashScopeReasoningContext.PopFrame();
            throw;
        }
    }

    private static bool IsEventStream(HttpResponseMessage response)
    {
        var mt = response.Content.Headers.ContentType?.MediaType;
        return mt != null && mt.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
    }
}
