using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenWorkmate.AI.Gateway;
using OpenWorkmate.AI.Gateway.Storage;

namespace OpenWorkmate.AI.Gateway.Endpoints;

public static class MyDataEndpoints
{
    public static void MapMy(this WebApplication app, JsonSerializerOptions jsonOpts)
    {
        var my = app.MapGroup("/my");
        my.AddEndpointFilter(async (ctx, next) =>
        {
            var ip = ctx.HttpContext.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip))
                return Results.Json(new { ok = false, message = "Forbidden (loopback only)." }, statusCode: 403);
            return await next(ctx);
        });

        my.MapGet("/sessions", (SessionsIndex index) =>
        {
            var snap = index.Snapshot();
            var list = snap.OrderByDescending(kv => kv.Value.LastAt)
                .Select(kv => new
                {
                    sessionId = kv.Key,
                    kv.Value.FirstAt,
                    kv.Value.LastAt,
                    kv.Value.TraceCount,
                    kv.Value.SizeBytes,
                    kv.Value.Shards
                })
                .ToList();
            return Results.Json(list, jsonOpts);
        });

        my.MapGet("/sessions/{sessionId}/raw.jsonl", (string sessionId, IOptionsMonitor<AiGatewayOptions> opt) =>
        {
            var text = ReadSessionJsonlAll(opt, sessionId);
            if (text == null) return Results.NotFound();
            return Results.Text(text, "application/x-ndjson", Encoding.UTF8);
        });

        my.MapGet("/sessions/{sessionId}", (string sessionId, IOptionsMonitor<AiGatewayOptions> opt) =>
        {
            var text = ReadSessionJsonlAll(opt, sessionId);
            if (text == null) return Results.NotFound();
            var tree = BuildTraceTree(text);
            return Results.Json(tree, jsonOpts);
        });

        my.MapDelete("/traces/{traceId}", (string traceId, HttpContext http, IOptionsMonitor<AiGatewayOptions> opt) =>
        {
            var sid = (http.Request.Query["sid"].ToString() ?? "").Trim();
            if (string.IsNullOrEmpty(sid))
                return Results.Json(new { ok = false, message = "sid query required." }, statusCode: 400);
            var removed = DeleteTraceFromSession(opt, sid, traceId);
            return Results.Json(new { ok = true, removed });
        });

        my.MapGet("/sessions/{sessionId}/export.md", async (string sessionId, IOptionsMonitor<AiGatewayOptions> opt, BlobStore blobs) =>
        {
            var text = ReadSessionJsonlAll(opt, sessionId);
            if (text == null) return Results.NotFound();
            var md = await BuildExportMarkdownAsync(text, blobs).ConfigureAwait(false);
            return Results.Text(md, "text/markdown; charset=utf-8", Encoding.UTF8);
        });

        my.MapDelete("/sessions/{sessionId}", (string sessionId, IOptionsMonitor<AiGatewayOptions> opt, SessionsIndex index) =>
        {
            var dir = Path.Combine(Path.GetFullPath(opt.CurrentValue.DataRoot), "sessions");
            foreach (var f in Directory.EnumerateFiles(dir, SanitizeSessionId(sessionId) + "*.jsonl", SearchOption.TopDirectoryOnly))
                try { File.Delete(f); } catch { /* ignore */ }
            index.RemoveSession(SanitizeSessionId(sessionId));
            return Results.Json(new { ok = true });
        });

        my.MapDelete("/sessions", (IOptionsMonitor<AiGatewayOptions> opt, SessionsIndex index) =>
        {
            var root = Path.GetFullPath(opt.CurrentValue.DataRoot);
            TryDeleteDir(Path.Combine(root, "sessions"));
            TryDeleteDir(Path.Combine(root, "blobs"));
            index.ClearAll();
            return Results.Json(new { ok = true });
        });
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    private static string SanitizeSessionId(string id)
    {
        var t = (id ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            t = t.Replace(c, '_');
        return string.IsNullOrEmpty(t) ? "unknown" : (t.Length > 120 ? t[..120] : t);
    }

    private static string? ReadSessionJsonlAll(IOptionsMonitor<AiGatewayOptions> opt, string sessionId)
    {
        var sid = SanitizeSessionId(sessionId);
        var dir = Path.Combine(Path.GetFullPath(opt.CurrentValue.DataRoot), "sessions");
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, sid + "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var f in files)
            sb.Append(File.ReadAllText(f));
        return sb.ToString();
    }

    private static object BuildTraceTree(string ndjson)
    {
        var events = new List<JsonElement>();
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                events.Add(doc.RootElement.Clone());
            }
            catch { /* skip */ }
        }

        var byTrace = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var orphans = new List<JsonElement>();
        foreach (var e in events)
        {
            if (e.TryGetProperty("traceId", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var tid = t.GetString() ?? "";
                if (!byTrace.TryGetValue(tid, out var list))
                    byTrace[tid] = list = new List<JsonElement>();
                list.Add(e);
            }
            else
            {
                orphans.Add(e);
            }
        }

        var traces = byTrace.Select(kv => new
        {
            traceId = kv.Key,
            eventCount = kv.Value.Count,
            startedAt = kv.Value
                .Select(el => el.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderBy(s => s, StringComparer.Ordinal)
                .FirstOrDefault(),
            events = kv.Value
        }).OrderBy(x => x.startedAt, StringComparer.Ordinal).ToList();

        return new { traces, orphans };
    }

    private static int DeleteTraceFromSession(IOptionsMonitor<AiGatewayOptions> opt, string sessionId, string traceId)
    {
        var sid = SanitizeSessionId(sessionId);
        var dir = Path.Combine(Path.GetFullPath(opt.CurrentValue.DataRoot), "sessions");
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.GetFiles(dir, sid + "*.jsonl", SearchOption.TopDirectoryOnly);
        var removed = 0;
        foreach (var file in files)
        {
            var tmp = file + ".tmp";
            using (var reader = new StreamReader(file, Encoding.UTF8))
            using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var keep = true;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("traceId", out var t) &&
                            t.ValueKind == JsonValueKind.String &&
                            string.Equals(t.GetString(), traceId, StringComparison.Ordinal))
                        {
                            keep = false;
                            removed++;
                        }
                    }
                    catch { /* keep malformed lines */ }
                    if (keep)
                    {
                        writer.Write(line);
                        writer.Write('\n');
                    }
                }
            }
            File.Move(tmp, file, overwrite: true);
        }
        return removed;
    }

    private static async Task<string> BuildExportMarkdownAsync(string ndjson, BlobStore blobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AI Gateway session export");
        sb.AppendLine();
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:O}");
        sb.AppendLine();
        var i = 0;
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            i++;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : "?";
                sb.AppendLine($"## Event {i}: `{kind}`");
                sb.AppendLine();
                if (root.TryGetProperty("traceId", out var tid))
                    sb.AppendLine($"- **traceId**: `{tid}`");
                if (root.TryGetProperty("model", out var model))
                    sb.AppendLine($"- **model**: `{model}`");
                if (root.TryGetProperty("latencyMs", out var lat))
                    sb.AppendLine($"- **latencyMs**: {lat}");
                await AppendBodyAsync(sb, root, "requestInline", "requestRef", blobs).ConfigureAwait(false);
                await AppendBodyAsync(sb, root, "responseInline", "responseRef", blobs).ConfigureAwait(false);
                sb.AppendLine();
                sb.AppendLine("<details><summary>Raw JSON</summary>");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
                sb.AppendLine("```");
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
            catch
            {
                sb.AppendLine($"## Event {i} (parse error)");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(line.Length > 2000 ? line[..2000] + "…" : line);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static async Task AppendBodyAsync(StringBuilder sb, JsonElement root, string inlineProp, string refProp, BlobStore blobs)
    {
        if (root.TryGetProperty(inlineProp, out var inl) && inl.ValueKind == JsonValueKind.String)
        {
            var s = inl.GetString() ?? "";
            if (s.Length > 12000)
            {
                sb.AppendLine($"### {inlineProp} (truncated)");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(s[..12000] + "…");
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine($"### {inlineProp}");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(s);
                sb.AppendLine("```");
            }
            sb.AppendLine();
            return;
        }
        if (root.TryGetProperty(refProp, out var r) && r.ValueKind == JsonValueKind.String)
        {
            var rr = r.GetString();
            var bytes = blobs.TryRead(rr);
            if (bytes == null)
            {
                sb.AppendLine($"### {refProp}: `{rr}` (blob missing)");
                sb.AppendLine();
                return;
            }
            var text = Encoding.UTF8.GetString(bytes);
            sb.AppendLine($"### {refProp}: `{rr}`");
            sb.AppendLine();
            sb.AppendLine("<details><summary>body</summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(text.Length > 24000 ? text[..24000] + "…" : text);
            sb.AppendLine("```");
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
