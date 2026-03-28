using System.Linq;
using System.Net.Http.Headers;
using OfficeCopilot.Server;

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

        var isStream = bodyBytes != null && DashScopeChatRequestMerge.RequestBodyIndicatesStream(bodyBytes);
        if (!isStream)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        DashScopeReasoningContext.PushFrame();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DashScopeReasoningContext.PopFrame();
                return response;
            }

            if (!IsEventStream(response))
            {
                DashScopeReasoningContext.PopFrame();
                return response;
            }

            try
            {
                var mediaType = response.Content.Headers.ContentType;
                var innerStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var tap = new DashScopeSseReasoningTapStream(innerStream, DashScopeReasoningContext.EnqueueReasoning);
                var wrapped = new PopFrameOnDisposeStream(tap, DashScopeReasoningContext.PopFrame);
                var newBody = new StreamContent(wrapped);
                if (mediaType != null)
                    newBody.Headers.ContentType = mediaType;
                response.Content = newBody;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DashScope SSE wrap failed for model entry {EntryId}", _modelEntryId);
                DashScopeReasoningContext.PopFrame();
                throw;
            }

            return response;
        }
        catch
        {
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
