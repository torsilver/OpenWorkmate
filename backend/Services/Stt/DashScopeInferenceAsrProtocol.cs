using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWorkmate.Server;

namespace OpenWorkmate.Server.Services.Stt;

/// <summary>
/// 阿里云百炼 <c>wss://.../api-ws/v1/inference</c> 实时语音识别协议（run-task / finish-task / result-generated）。
/// 字段以 <see href="https://help.aliyun.com/zh/model-studio/developer-reference/websocket-for-paraformer-real-time-service">Paraformer WebSocket API</see> 为准。
/// </summary>
public static class DashScopeInferenceAsrProtocol
{
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static int DefaultSampleRateForModel(string modelId)
    {
        var m = (modelId ?? "").Trim();
        return m.Contains("8k", StringComparison.OrdinalIgnoreCase) ? 8000 : 16000;
    }

    public static string BuildRunTaskJson(string taskId, RealtimeAsrConfig cfg, int sampleRate, string format, bool meetingMode)
    {
        var model = string.IsNullOrWhiteSpace(cfg.ModelId) ? "fun-asr-realtime" : cfg.ModelId.Trim();
        var parameters = new JsonObject
        {
            ["format"] = format,
            ["sample_rate"] = sampleRate
        };

        var mid = model.ToLowerInvariant();
        if (mid.Contains("paraformer", StringComparison.Ordinal))
        {
            if (cfg.DisfluencyRemovalEnabled)
                parameters["disfluency_removal_enabled"] = true;
            if (cfg.LanguageHints is { Count: > 0 })
            {
                var arr = new JsonArray();
                foreach (var h in cfg.LanguageHints)
                {
                    var t = (h ?? "").Trim();
                    if (t.Length > 0) arr.Add(JsonValue.Create(t));
                }
                if (arr.Count > 0)
                    parameters["language_hints"] = arr;
            }
            if (mid.Contains("paraformer-realtime-v2", StringComparison.Ordinal) || mid.Contains("paraformer-realtime-8k-v2", StringComparison.Ordinal))
            {
                parameters["semantic_punctuation_enabled"] = meetingMode || cfg.SemanticPunctuationEnabled;
                if (cfg.Heartbeat)
                    parameters["heartbeat"] = true;
            }
        }
        else
        {
            // Fun-ASR 等：长会议时开启 heartbeat，避免静音超时（文档：部分模型支持）
            if (cfg.Heartbeat)
                parameters["heartbeat"] = true;
        }

        var root = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["action"] = "run-task",
                ["task_id"] = taskId,
                ["streaming"] = "duplex"
            },
            ["payload"] = new JsonObject
            {
                ["task_group"] = "audio",
                ["task"] = "asr",
                ["function"] = "recognition",
                ["model"] = model,
                ["input"] = new JsonObject(),
                ["parameters"] = parameters
            }
        };

        return root.ToJsonString(WriteOpts);
    }

    public static string BuildFinishTaskJson(string taskId)
    {
        var root = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["action"] = "finish-task",
                ["task_id"] = taskId,
                ["streaming"] = "duplex"
            },
            ["payload"] = new JsonObject
            {
                ["input"] = new JsonObject()
            }
        };
        return root.ToJsonString(WriteOpts);
    }

    public static bool TryParseUpstreamEvent(string json, out string? eventName, out string? errorMessage, out string? sentenceText, out bool sentenceEnd, out bool skipForHeartbeat)
    {
        eventName = null;
        errorMessage = null;
        sentenceText = null;
        sentenceEnd = false;
        skipForHeartbeat = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object)
                return false;

            if (header.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String)
                eventName = ev.GetString();

            if (string.Equals(eventName, "task-failed", StringComparison.Ordinal))
            {
                if (header.TryGetProperty("error_message", out var em) && em.ValueKind == JsonValueKind.String)
                    errorMessage = em.GetString();
                else if (header.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.String)
                    errorMessage = ec.GetString();
                return true;
            }

            if (!string.Equals(eventName, "result-generated", StringComparison.Ordinal))
                return !string.IsNullOrEmpty(eventName);

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return true;

            if (!payload.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object)
                return true;

            if (!output.TryGetProperty("sentence", out var sentence) || sentence.ValueKind != JsonValueKind.Object)
                return true;

            if (sentence.TryGetProperty("heartbeat", out var hb) && hb.ValueKind == JsonValueKind.True)
            {
                skipForHeartbeat = true;
                return true;
            }

            if (sentence.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                sentenceText = tx.GetString();

            if (sentence.TryGetProperty("sentence_end", out var se) && se.ValueKind is JsonValueKind.True or JsonValueKind.False)
                sentenceEnd = se.GetBoolean();

            return true;
        }
        catch
        {
            return false;
        }
    }
}
