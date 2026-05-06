using System.Net.Http.Headers;
using System.Text;
using OfficeCopilot.Server.Services.Chat;
using OfficeCopilot.Server.Services.DashScope;
using OfficeCopilot.Server.Services.ModelProfiles;

namespace OfficeCopilot.Server.Services.OpenAiCompat;

/// <summary>
/// 非百炼直连 OpenAI 兼容链：流式解析 <c>reasoning_content</c>、在带 tool_calls 的轮次结束后向 echo 存储追加一轮推理全文；
/// 出站为缺字段的 assistant+tool_calls 注入上一轮推理全文；可选 Kimi <c>thinking.keep</c>（见 ModelProfile）。
/// </summary>
internal sealed class OpenAiReasoningEchoHandler : DelegatingHandler
{
    private readonly ConfigService _configService;
    private readonly string _modelEntryId;
    private readonly ModelProfileRegistry _registry;
    private readonly TimelineBlockStreamCoordinator? _timelineBlockCoordinator;
    private readonly ILogger<OpenAiReasoningEchoHandler>? _logger;

    public OpenAiReasoningEchoHandler(
        ConfigService configService,
        string modelEntryId,
        ModelProfileRegistry registry,
        HttpMessageHandler inner,
        TimelineBlockStreamCoordinator? timelineBlockCoordinator = null,
        ILogger<OpenAiReasoningEchoHandler>? logger = null)
        : base(inner)
    {
        _configService = configService;
        _modelEntryId = modelEntryId ?? "";
        _registry = registry;
        _timelineBlockCoordinator = timelineBlockCoordinator;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post
            || request.Content is null
            || !IsChatCompletions(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (DashScopeChatRequestMerge.IsDashScopeChatCompletions(request.RequestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var entry = ResolveEntry();
        _registry.TryGetMergedForModelEntry(entry, out var profile);
        if (profile?.DisableReasoningHttpEcho == true)
        {
            _logger?.LogDebug(
                "[OpenAiReasoningEcho] disabled by profile entry={Entry} session={Session} uri={Uri}",
                _modelEntryId,
                SessionContext.GetSessionId() ?? "(null)",
                request.RequestUri?.GetLeftPart(UriPartial.Path));
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var sessionId = SessionContext.GetSessionId();
        byte[] bodyBytes;
        try
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var streamReq = DashScopeChatRequestMerge.RequestBodyIndicatesStream(bodyBytes);
        if (streamReq)
        {
            OpenAiReasoningEchoDiagnostics.LogChatCompletionsMessagesOutline(
                _logger,
                string.IsNullOrEmpty(sessionId) ? "outbound_before_echo_patch(no_session_skipped)" : "outbound_before_echo_patch",
                _modelEntryId,
                sessionId,
                bodyBytes);
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            var patched = OpenAiReasoningEchoMessagePatch.TryPatchRequestUtf8(bodyBytes, sessionId, profile, _logger);
            if (patched != null)
            {
                var newContent = new ByteArrayContent(patched);
                CopyContentHeaders(request.Content, newContent);
                request.Content = newContent;
                bodyBytes = patched;
                if (streamReq)
                {
                    OpenAiReasoningEchoDiagnostics.LogChatCompletionsMessagesOutline(
                        _logger,
                        "outbound_after_echo_patch",
                        _modelEntryId,
                        sessionId,
                        bodyBytes);
                }
            }
        }
        else if (streamReq)
        {
            _logger?.LogDebug(
                "[OpenAiReasoningEcho] no SessionContext sessionId — echo patch skipped entry={Entry} uri={Uri}",
                _modelEntryId,
                request.RequestUri?.GetLeftPart(UriPartial.Path));
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && streamReq)
            await TryLogUpstreamChatCompletionErrorAsync(response, request.RequestUri, sessionId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(sessionId)
            || bodyBytes.Length == 0
            || !DashScopeChatRequestMerge.RequestBodyIndicatesStream(bodyBytes))
            return response;

        if (!response.IsSuccessStatusCode)
            return response;

        if (!IsEventStream(response))
            return response;

        var bridgeSessionId = sessionId;
        var reasoningQueue = DashScopeReasoningContext.PushFrame();
        DashScopeReasoningSessionBridge.AttachQueue(bridgeSessionId, reasoningQueue);
        if (_timelineBlockCoordinator != null)
            _timelineBlockCoordinator.OnMainChatReasoningSourceAttached(bridgeSessionId);

        var acc = new StringBuilder(256);
        var sawToolCalls = false;
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
                    acc.Append(fragment);
                    reasoningQueue.Enqueue(fragment);
                },
                telemetry,
                jsonLine =>
                {
                    if (OpenAiReasoningEchoSseHelpers.JsonLineIndicatesToolCalls(jsonLine))
                        sawToolCalls = true;
                });
            var wrapped = new PopFrameOnDisposeStream(tap, () =>
            {
                DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
                if (sawToolCalls)
                {
                    OpenAiReasoningEchoStore.AppendAfterToolAssistantRound(bridgeSessionId, acc.ToString());
                    _logger?.LogDebug(
                        "[OpenAiReasoningEcho] echo_store appended_round session={Session} totalRounds={Rounds} reasoningChars={Chars}",
                        bridgeSessionId,
                        OpenAiReasoningEchoStore.GetSessionReasoningRoundCount(bridgeSessionId),
                        acc.Length);
                }

                _logger?.LogDebug(
                    "[OpenAiReasoningEcho] SSE closed entry={Entry} session={Session} sawToolCalls={Tools} reasoningChars={Chars} sseLines={Lines}",
                    _modelEntryId,
                    bridgeSessionId,
                    sawToolCalls,
                    acc.Length,
                    telemetry.SseDataLines);
                DashScopeReasoningContext.PopFrame();
            });
            var newBody = new StreamContent(wrapped);
            if (mediaType != null)
                newBody.Headers.ContentType = mediaType;
            response.Content = newBody;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OpenAiReasoningEcho] SSE wrap failed entry={EntryId}", _modelEntryId);
            DashScopeReasoningSessionBridge.TryDetachQueue(bridgeSessionId, reasoningQueue);
            DashScopeReasoningContext.PopFrame();
            throw;
        }

        return response;
    }

    private async Task TryLogUpstreamChatCompletionErrorAsync(
        HttpResponseMessage response,
        Uri? requestUri,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (_logger is null)
            return;
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var preview = "(empty)";
            if (bytes.Length > 0)
            {
                var n = Math.Min(bytes.Length, 900);
                preview = Encoding.UTF8.GetString(bytes.AsSpan(0, n));
                if (bytes.Length > n)
                    preview += "…";
            }

            _logger.LogWarning(
                "[OpenAiReasoningEcho] upstream_error entry={Entry} session={Session} status={Status} uriHost={Host} bodyLen={Len} preview={Preview}",
                _modelEntryId,
                sessionId ?? "(null)",
                (int)response.StatusCode,
                requestUri?.Host ?? "(null)",
                bytes.Length,
                preview);

            if (bytes.Length > 0)
            {
                var mediaType = response.Content.Headers.ContentType;
                var restored = new ByteArrayContent(bytes);
                if (mediaType != null)
                    restored.Headers.ContentType = mediaType;
                response.Content = restored;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OpenAiReasoningEcho] upstream_error body read failed entry={Entry}", _modelEntryId);
        }
    }

    private static void CopyContentHeaders(HttpContent from, HttpContent to)
    {
        foreach (var h in from.Headers)
            to.Headers.TryAddWithoutValidation(h.Key, h.Value);
    }

    private static bool IsChatCompletions(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;
        return uri.AbsolutePath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEventStream(HttpResponseMessage response)
    {
        var mt = response.Content.Headers.ContentType?.MediaType;
        return mt != null && mt.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private AiModelEntry? ResolveEntry()
    {
        var list = _configService.Current.AiModels;
        if (list is null || list.Count == 0)
            return null;
        return list.FirstOrDefault(e => string.Equals(e.Id, _modelEntryId, StringComparison.OrdinalIgnoreCase));
    }
}
