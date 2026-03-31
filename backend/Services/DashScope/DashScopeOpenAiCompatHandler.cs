using System.Linq;
using System.Net.Http.Headers;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>
/// 百炼 OpenAI 兼容：合并 chat/completions 请求体中的扩展字段，并对流式 SSE 响应旁路解析 <c>reasoning_content</c>。
/// 每个对话模型条目使用独立 <see cref="HttpClient"/> 挂载本 Handler，以便按 <see cref="OfficeCopilot.Server.AiModelEntry"/> 区分。
/// </summary>
internal sealed class DashScopeOpenAiCompatHandler : DelegatingHandler
{
    private readonly ConfigService _configService;
    private readonly string _modelEntryId;
    private readonly ILogger<DashScopeOpenAiCompatHandler>? _logger;

    public DashScopeOpenAiCompatHandler(
        ConfigService configService,
        string modelEntryId,
        HttpMessageHandler inner,
        ILogger<DashScopeOpenAiCompatHandler>? logger = null)
        : base(inner)
    {
        _configService = configService;
        _modelEntryId = modelEntryId ?? "";
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!DashScopeChatRequestMerge.IsDashScopeChatCompletions(request.RequestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        byte[]? bodyBytes = null;
        if (request.Content != null)
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var entry = ResolveEntry();
        var merged = DashScopeChatRequestMerge.MergeChatCompletionUtf8Body(bodyBytes.AsSpan(), entry);
        if (merged != null)
        {
            var newContent = new ByteArrayContent(merged);
            if (request.Content?.Headers != null)
            {
                foreach (var h in request.Content.Headers)
                    newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            request.Content = newContent;
        }

        var effectiveBody = merged ?? bodyBytes ?? Array.Empty<byte>();
        DashScopeChatRequestDiagnostics.LogOutgoingBody(
            _logger,
            _modelEntryId,
            DashScopeCallKindContext.IsBackground,
            entry,
            effectiveBody,
            merged != null);

        var isStream = bodyBytes != null && DashScopeChatRequestMerge.RequestBodyIndicatesStream(bodyBytes);
        if (!isStream)
        {
            _logger?.LogWarning(
                "[DashScope] stream=false in outbound body (SSE reasoning tap will NOT run) entry={Entry}",
                _modelEntryId);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var reasoningQueue = DashScopeReasoningContext.PushFrame();
        var bridgeSessionId = SessionContext.GetSessionId();
        DashScopeReasoningSessionBridge.AttachQueue(bridgeSessionId, reasoningQueue);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var contentTypeHdr = response.Content.Headers.ContentType?.ToString() ?? "(null)";
            var isEvent = IsEventStream(response);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogInformation(
                    "[DashScope] resp entry={Entry} status={Status} contentType={Ct} (no SSE tap)",
                    _modelEntryId, (int)response.StatusCode, contentTypeHdr);
                DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                DashScopeReasoningContext.PopFrame();
                return response;
            }

            if (!isEvent)
            {
                _logger?.LogWarning(
                    "[DashScope] resp entry={Entry} contentType={Ct} isNotEventStream=true — SSE reasoning tap SKIPPED (reasoning_content will not be parsed)",
                    _modelEntryId, contentTypeHdr);
                DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                DashScopeReasoningContext.PopFrame();
                return response;
            }

            _logger?.LogInformation(
                "[DashScope] resp entry={Entry} contentType={Ct} attaching SSE reasoning tap",
                _modelEntryId, contentTypeHdr);

            try
            {
                var mediaType = response.Content.Headers.ContentType;
                var innerStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var telemetry = new DashScopeSseTapTelemetry();
                var tap = new DashScopeSseReasoningTapStream(innerStream, fragment =>
                {
                    if (string.IsNullOrEmpty(fragment))
                        return;
                    reasoningQueue.Enqueue(fragment);
                }, telemetry);
                var wrapped = new PopFrameOnDisposeStream(tap, () =>
                {
                    DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                    _logger?.LogInformation(
                        "[DashScope] SSE tap closed entry={Entry}: sseDataLines={DataLines} choiceChunks={Choices} reasoningParsed={Parsed} jsonErrors={JsonErr} enqueueDroppedNoFrame={Dropped}",
                        _modelEntryId,
                        telemetry.SseDataLines,
                        telemetry.ChoiceChunksSeen,
                        telemetry.ReasoningFragmentsParsed,
                        telemetry.JsonParseErrors,
                        telemetry.EnqueueDroppedNoAsyncLocalFrame);
                    for (var i = 0; i < telemetry.SsePayloadPreviews.Count; i++)
                    {
                        _logger?.LogInformation(
                            "[DashScope] resp entry={Entry} ssePayloadPreview[{Index}]={Chunk}",
                            _modelEntryId,
                            i,
                            telemetry.SsePayloadPreviews[i]);
                    }

                    DashScopeReasoningContext.PopFrame();
                });
                var newBody = new StreamContent(wrapped);
                if (mediaType != null)
                    newBody.Headers.ContentType = mediaType;
                response.Content = newBody;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DashScope SSE wrap failed for model entry {EntryId}", _modelEntryId);
                DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                DashScopeReasoningContext.PopFrame();
                throw;
            }

            return response;
        }
        catch
        {
            DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
            DashScopeReasoningContext.PopFrame();
            throw;
        }
    }

    private AiModelEntry? ResolveEntry()
    {
        var list = _configService.Current.AiModels;
        if (list == null || list.Count == 0)
            return null;
        return list.FirstOrDefault(e => string.Equals(e.Id, _modelEntryId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEventStream(HttpResponseMessage response)
    {
        var mt = response.Content.Headers.ContentType?.MediaType;
        return mt != null && mt.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
    }
}
