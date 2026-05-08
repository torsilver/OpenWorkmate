using System.Net;
using System.Text.Json;
using OpenWorkmate.AI.Gateway;
using OpenWorkmate.AI.Gateway.Storage;

namespace OpenWorkmate.AI.Gateway.Endpoints;

public static class IngestEndpoints
{
    public static void MapIngest(this WebApplication app, JsonSerializerOptions jsonOpts)
    {
        app.MapPost("/ingest/spans", async (HttpContext http, SessionJsonlWriter jsonl, IConfiguration cfg) =>
            {
                if (!TelemetryAuth.ValidatePolicyApiKey(http, cfg))
                    return Results.Json(new { ok = false, message = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
                IngestBatch? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<IngestBatch>(http.Request.Body, jsonOpts, http.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.Json(new { ok = false, message = "Invalid JSON." }, statusCode: 400);
                }
                if (body?.Events is not { Count: > 0 })
                    return Results.Json(new { ok = true, written = 0 });
                var n = 0;
                foreach (var ev in body.Events)
                {
                    if (ev.ValueKind != JsonValueKind.Object) continue;
                    if (!ev.TryGetProperty("sessionId", out var sidEl) || sidEl.ValueKind != JsonValueKind.String)
                        continue;
                    var sid = (sidEl.GetString() ?? "").Trim();
                    if (string.IsNullOrEmpty(sid)) continue;
                    jsonl.AppendLine(sid, ev.GetRawText());
                    n++;
                }
                return Results.Json(new { ok = true, written = n });
            })
            .WithName("IngestSpans")
            .WithTags("ingest");

        app.MapPost("/ingest/scores", async (HttpContext http, SessionJsonlWriter jsonl, IConfiguration cfg) =>
            {
                var ip = http.Connection.RemoteIpAddress;
                var isLoopback = ip != null && IPAddress.IsLoopback(ip);
                if (!isLoopback && !TelemetryAuth.ValidatePolicyApiKey(http, cfg))
                    return Results.Json(new { ok = false, message = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
                ScoreIngest? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<ScoreIngest>(http.Request.Body, jsonOpts, http.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.Json(new { ok = false, message = "Invalid JSON." }, statusCode: 400);
                }
                if (body is null || string.IsNullOrWhiteSpace(body.SessionId))
                    return Results.Json(new { ok = false, message = "sessionId required." }, statusCode: 400);
                var line = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["kind"] = "score",
                    ["createdAt"] = DateTime.UtcNow.ToString("O"),
                    ["traceId"] = body.TraceId,
                    ["spanId"] = body.SpanId,
                    ["name"] = body.Name ?? "user_thumb",
                    ["value"] = body.Value,
                    ["source"] = body.Source ?? "user",
                    ["comment"] = body.Comment
                }, jsonOpts);
                jsonl.AppendLine(body.SessionId.Trim(), line);
                return Results.Json(new { ok = true });
            })
            .WithName("IngestScores")
            .WithTags("ingest");
    }
}

public sealed class IngestBatch
{
    public List<JsonElement>? Events { get; set; }
}

public sealed class ScoreIngest
{
    public string? SessionId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? Name { get; set; }
    public double? Value { get; set; }
    public string? Source { get; set; }
    public string? Comment { get; set; }
}
