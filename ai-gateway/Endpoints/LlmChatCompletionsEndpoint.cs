using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenWorkmate.AI.Gateway.Storage;

namespace OpenWorkmate.AI.Gateway.Endpoints;

public static class LlmChatCompletionsEndpoint
{
    private const string DefaultDashScopeBase = "https://dashscope.aliyuncs.com/compatible-mode/v1";

    public static void MapLlmChatCompletions(this WebApplication app)
    {
        app.MapPost("/llm/v1/chat/completions", HandleAsync)
            .DisableAntiforgery()
            .WithName("LlmChatCompletions")
            .WithTags("llm");
    }

    private static async Task HandleAsync(
        HttpContext http,
        IHttpClientFactory httpFactory,
        SessionJsonlWriter jsonl,
        BlobStore blobs)
    {
        var ct = http.RequestAborted;
        byte[] reqBytes;
        await using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            reqBytes = ms.ToArray();
        }

        var upstreamBase = (http.Request.Headers["X-AI-Upstream-Base"].FirstOrDefault() ?? "").Trim();
        if (string.IsNullOrEmpty(upstreamBase))
            upstreamBase = DefaultDashScopeBase;
        var targetUrl = upstreamBase.TrimEnd('/') + "/chat/completions";

        using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl);
        req.Content = new ByteArrayContent(reqBytes);
        var incomingCt = http.Request.ContentType;
        if (!string.IsNullOrEmpty(incomingCt))
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(incomingCt);
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        var client = httpFactory.CreateClient("llm-upstream");
        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await http.Response.WriteAsJsonAsync(new { ok = false, message = ex.Message }, cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        var sessionId = (http.Request.Headers["X-AI-Session-Id"].FirstOrDefault() ?? "").Trim();
        if (string.IsNullOrEmpty(sessionId)) sessionId = "unknown-session";
        var traceId = (http.Request.Headers["X-AI-Trace-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N")).Trim();
        var spanId = (http.Request.Headers["X-AI-Span-Id"].FirstOrDefault() ?? traceId).Trim();
        var vendor = (http.Request.Headers["X-AI-Vendor"].FirstOrDefault() ?? "dashscope").Trim().ToLowerInvariant();
        var llmCallId = Guid.NewGuid().ToString("N");

        http.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
        {
            if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            http.Response.Headers[h.Key] = h.Value.ToArray();
        }
        foreach (var h in resp.Content.Headers)
            http.Response.Headers[h.Key] = h.Value.ToArray();
        http.Response.Headers["X-AI-Llm-Call-Id"] = llmCallId;

        var isEventStream = resp.Content.Headers.ContentType?.MediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;
        byte[] respBytes;
        if (isEventStream)
        {
            await using var upstreamStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var accum = new MemoryStream();
            var buffer = new byte[65536];
            int n;
            while ((n = await upstreamStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await accum.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            respBytes = accum.ToArray();
        }
        else
        {
            respBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            await http.Response.Body.WriteAsync(respBytes, ct).ConfigureAwait(false);
        }
        sw.Stop();

        var model = TryReadModel(reqBytes);
        var streamFrames = isEventStream ? CountSseDataLines(respBytes) : 0;
        var (promptTok, completionTok) = TryReadUsage(respBytes, isEventStream);

        var reqForStore = StripSensitiveFromChatRequestJson(reqBytes);
        var reqRef = blobs.TryStoreRef(reqForStore);
        var respRef = blobs.TryStoreRef(respBytes);
        var reqInline = reqRef == null ? Encoding.UTF8.GetString(reqForStore) : null;
        var respInline = respRef == null ? Encoding.UTF8.GetString(respBytes) : null;

        var line = new Dictionary<string, object?>
        {
            ["kind"] = "llm_call",
            ["createdAt"] = DateTime.UtcNow.ToString("O"),
            ["traceId"] = traceId,
            ["spanId"] = spanId,
            ["parentSpanId"] = string.Equals(spanId, traceId, StringComparison.Ordinal)
                ? null
                : (http.Request.Headers["X-AI-Parent-Span-Id"].FirstOrDefault()),
            ["llmCallId"] = llmCallId,
            ["vendor"] = vendor,
            ["model"] = model,
            ["endpoint"] = targetUrl,
            ["requestRef"] = reqRef,
            ["requestInline"] = reqInline,
            ["responseRef"] = respRef,
            ["responseInline"] = respInline,
            ["promptTokens"] = promptTok,
            ["completionTokens"] = completionTok,
            ["latencyMs"] = (int)sw.ElapsedMilliseconds,
            ["status"] = (int)resp.StatusCode,
            ["streamFrames"] = streamFrames,
            ["startedAt"] = DateTime.UtcNow.AddMilliseconds(-sw.ElapsedMilliseconds).ToString("O"),
            ["endedAt"] = DateTime.UtcNow.ToString("O")
        };
        if (string.Equals(spanId, traceId, StringComparison.Ordinal)
            && string.IsNullOrEmpty(http.Request.Headers["X-AI-Parent-Span-Id"].FirstOrDefault()))
            line["attributes"] = new Dictionary<string, object?> { ["orphan"] = true };

        var jsonLine = JsonSerializer.Serialize(line);
        try
        {
            jsonl.AppendLine(sessionId, jsonLine);
        }
        catch
        {
            /* best-effort */
        }
    }

    private static byte[] StripSensitiveFromChatRequestJson(byte[] utf8)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return utf8;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var n = p.Name;
                    if (n.Equals("api_key", StringComparison.OrdinalIgnoreCase)) continue;
                    if (n.Equals("apikey", StringComparison.OrdinalIgnoreCase)) continue;
                    writer.WritePropertyName(n);
                    p.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
        catch
        {
            return utf8;
        }
    }

    private static string TryReadModel(ReadOnlySpan<byte> reqUtf8)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(reqUtf8));
            if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? "";
        }
        catch
        {
            /* ignore */
        }
        return "";
    }

    private static int CountSseDataLines(ReadOnlySpan<byte> respUtf8)
    {
        var text = Encoding.UTF8.GetString(respUtf8);
        var n = 0;
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith("data:", StringComparison.Ordinal)) n++;
        }
        return n;
    }

    private static (int? prompt, int? completion) TryReadUsage(ReadOnlySpan<byte> respUtf8, bool isSse)
    {
        try
        {
            if (!isSse)
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(respUtf8));
                return ReadUsageFromRoot(doc.RootElement);
            }
            var text = Encoding.UTF8.GetString(respUtf8);
            foreach (var line in text.Split('\n').Reverse())
            {
                var t = line.TrimEnd('\r').Trim();
                if (!t.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = t["data:".Length..].Trim();
                if (payload == "[DONE]") continue;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var (p, c) = ReadUsageFromRoot(doc.RootElement);
                    if (p != null || c != null) return (p, c);
                }
                catch
                {
                    /* next line */
                }
            }
        }
        catch
        {
            /* ignore */
        }
        return (null, null);
    }

    private static (int? prompt, int? completion) ReadUsageFromRoot(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u)) return (null, null);
        int? p = u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pv) ? pv : null;
        int? c = u.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cv) ? cv : null;
        return (p, c);
    }
}
