using System.Text.Json;

namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>按会话档位与中继传输策略在入队前裁剪；持久化以 Seq 为准（经 Serilog 写入）。</summary>
public static class TelemetryOutboundRedactor
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static (string? Message, JsonElement? Payload) Apply(
        string tier,
        string eventType,
        string? message,
        JsonElement? payload,
        TelemetryTransmissionPolicyFile policy)
    {
        var t = (tier ?? "").Trim().ToLowerInvariant();
        if (t is "off") return (message, payload);

        var msg = message ?? "";
        if (t == "minimal")
        {
            if (string.Equals(eventType, "assistant_turn_final", StringComparison.Ordinal))
                msg = Truncate(msg, policy.Minimal.AssistantTurnFinalMsgPreviewMax);
            else if (string.Equals(eventType, "tool_invocation_end", StringComparison.Ordinal))
                msg = Truncate(msg, policy.Minimal.ToolInvocationEndMsgPreviewMax);
            else
                msg = Truncate(msg, policy.Minimal.OtherEventMsgMax);
            return (msg, payload);
        }

        if (t == "traces")
        {
            msg = Truncate(msg, policy.Traces.MsgMax);
            return (msg, TruncatePayload(payload, policy.Traces.PayloadMax));
        }

        if (t == "full")
        {
            msg = Truncate(msg, policy.Full.MsgMax);
            return (msg, TruncatePayload(payload, policy.Full.PayloadMax));
        }

        msg = Truncate(msg, policy.Minimal.OtherEventMsgMax);
        return (msg, payload);
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        return s[..maxLen] + "…";
    }

    private static JsonElement? TruncatePayload(JsonElement? payload, int maxChars)
    {
        if (payload is not { } p) return null;
        if (p.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return payload;
        var s = JsonSerializer.Serialize(p, PayloadJson);
        if (s.Length <= maxChars) return payload;
        var cut = Truncate(s, maxChars);
        return JsonSerializer.SerializeToElement(cut);
    }
}
