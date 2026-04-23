using System.Text;
using System.Text.Json;

namespace OfficeCopilot.Server.Services.ModelProfiles;

/// <summary>
/// 对任意 OpenAI 兼容 <c>POST .../chat/completions</c>：当模型能力表要求且消息中存在
/// 「assistant + tool_calls 但缺少 reasoning_content」时，在请求体顶层写入 <c>thinking: false</c>，
/// 以规避部分供应商（如 Kimi thinking）在工具延续轮上的 400。
/// </summary>
internal sealed class ModelProfileChatRequestMergeHandler : DelegatingHandler
{
    private readonly ConfigService _configService;
    private readonly string _modelEntryId;
    private readonly ModelProfileRegistry _registry;
    private readonly ILogger<ModelProfileChatRequestMergeHandler>? _logger;

    public ModelProfileChatRequestMergeHandler(
        ConfigService configService,
        string modelEntryId,
        ModelProfileRegistry registry,
        HttpMessageHandler inner,
        ILogger<ModelProfileChatRequestMergeHandler>? logger = null)
        : base(inner)
    {
        _configService = configService;
        _modelEntryId = modelEntryId ?? "";
        _registry = registry;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null
            || request.Method != HttpMethod.Post
            || !IsChatCompletions(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var entry = ResolveEntry();
        if (!_registry.TryGetMergedForModelEntry(entry, out var profile)
            || profile is not { SuppressUpstreamThinkingWithTools: true })
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        byte[] bodyBytes;
        try
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var merged = TryMergeThinkingSuppress(bodyBytes);
        if (merged == null)
        {
            var newContent = new ByteArrayContent(bodyBytes);
            CopyContentHeaders(request.Content, newContent);
            request.Content = newContent;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        _logger?.LogDebug(
            "[ModelProfile] entry={Entry} profile={Profile}: injected thinking=false for tool continuation (missing reasoning_content).",
            _modelEntryId,
            profile.ProfileKey);

        var replaced = new ByteArrayContent(merged);
        CopyContentHeaders(request.Content, replaced);
        request.Content = replaced;
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void CopyContentHeaders(HttpContent from, HttpContent to)
    {
        foreach (var h in from.Headers)
            to.Headers.TryAddWithoutValidation(h.Key, h.Value);
    }

    private AiModelEntry? ResolveEntry()
    {
        var list = _configService.Current.AiModels;
        if (list is null) return null;
        return list.FirstOrDefault(e => string.Equals(e.Id, _modelEntryId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsChatCompletions(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        return uri.AbsolutePath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>若已抑制则返回 null（表示沿用原 bytes）；若改写则返回新 UTF-8。</summary>
    internal static byte[]? TryMergeThinkingSuppress(ReadOnlySpan<byte> bodyUtf8)
    {
        if (bodyUtf8.IsEmpty) return null;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bodyUtf8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;
            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return null;

            if (!HasAssistantToolCallsMissingReasoning(messages))
                return null;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                foreach (var p in root.EnumerateObject())
                {
                    if (string.Equals(p.Name, "thinking", StringComparison.Ordinal))
                        continue;
                    writer.WritePropertyName(p.Name);
                    p.Value.WriteTo(writer);
                }

                writer.WritePropertyName("thinking");
                writer.WriteBooleanValue(false);
                writer.WriteEndObject();
            }

            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAssistantToolCallsMissingReasoning(JsonElement messages)
    {
        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            if (!m.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                continue;
            if (!string.Equals(roleEl.GetString(), "assistant", StringComparison.Ordinal))
                continue;

            if (!m.TryGetProperty("tool_calls", out var tc) || tc.ValueKind != JsonValueKind.Array || tc.GetArrayLength() == 0)
                continue;

            if (!m.TryGetProperty("reasoning_content", out var rc))
                return true;
            if (rc.ValueKind == JsonValueKind.Null) return true;
            if (rc.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(rc.GetString()))
                return true;
        }

        return false;
    }
}
