using System.Text.Json;
using Taskly.Telemetry.Relay.Models;

namespace Taskly.Telemetry.Relay.Services;

public static class TelemetryLineFormatter
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string FormatLine(
        IngestEvent ev,
        EffectivePolicy policy,
        int maxChars,
        Random random)
    {
        var ts = (ev.TimestampUtc ?? DateTime.UtcNow).ToString("O");
        var type = ev.EventType ?? "unknown";
        var detail = (ev.DetailLevel ?? "p0").Trim().ToLowerInvariant();
        var isP2 = detail is "p2" or "full";

        if (policy.EffectiveTier == TelemetryTier.Off)
            return "";

        if (isP2 && random.NextDouble() > policy.P2BodySampleRate)
            return $"{ts}\t{type}\t[redacted_p2_sample]\tsession={ev.SessionId}";

        if (random.NextDouble() > policy.EventSampleRate && isP2)
            return "";

        var msg = ev.Message ?? "";
        var payloadStr = ev.Payload.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? JsonSerializer.Serialize(ev.Payload, PayloadJson)
            : "";

        return policy.EffectiveTier switch
        {
            TelemetryTier.Off => "",
            TelemetryTier.Minimal => FormatMinimal(ts, type, ev, msg),
            TelemetryTier.Traces => FormatTraces(ts, type, ev, msg, payloadStr, maxChars),
            TelemetryTier.Full => FormatFull(ts, type, ev, msg, payloadStr, maxChars),
            _ => ""
        };
    }

    private static string FormatMinimal(string ts, string type, IngestEvent ev, string msg)
    {
        var ct = ev.ClientType ?? "";
        var mid = ev.ModelId ?? "";

        if (string.Equals(type, "assistant_turn_final", StringComparison.Ordinal))
        {
            var charCount = msg.Length;
            var truncated = false;
            if (ev.Payload.ValueKind == JsonValueKind.Object)
            {
                if (ev.Payload.TryGetProperty("charCount", out var ccEl) && ccEl.TryGetInt32(out var cc))
                    charCount = cc;
                if (ev.Payload.TryGetProperty("truncated", out var trEl)
                    && trEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    truncated = trEl.ValueKind == JsonValueKind.True;
            }

            var preview = TelemetryPathValidator.SanitizeForLog(msg, 240);
            return $"{ts}\t{type}\tclientType={ct}\tmodelId={mid}\tcharCount={charCount}\ttruncated={truncated}\tmsgPreview={preview}\tsession={ev.SessionId}";
        }

        if (string.Equals(type, "tool_invocation_end", StringComparison.Ordinal))
        {
            var preview = TelemetryPathValidator.SanitizeForLog(msg, 120);
            return $"{ts}\t{type}\tclientType={ct}\tmodelId={mid}\tmsgLen={msg.Length}\tmsgPreview={preview}\tsession={ev.SessionId}";
        }

        var len = msg.Length;
        return $"{ts}\t{type}\tclientType={ct}\tmodelId={mid}\tmsgLen={len}\tsession={ev.SessionId}";
    }

    private static string FormatTraces(string ts, string type, IngestEvent ev, string msg, string payload, int max)
    {
        var m = TelemetryPathValidator.SanitizeForLog(msg, Math.Min(500, max));
        var p = payload.Length == 0 ? "" : TelemetryPathValidator.SanitizeForLog(payload, Math.Min(2000, max));
        return $"{ts}\t{type}\tsession={ev.SessionId}\tmsg={m}\tpayload={p}";
    }

    private static string FormatFull(string ts, string type, IngestEvent ev, string msg, string payload, int max)
    {
        var m = TelemetryPathValidator.SanitizeForLog(msg, max);
        var p = payload.Length == 0 ? "" : TelemetryPathValidator.SanitizeForLog(payload, max);
        return $"{ts}\t{type}\tsession={ev.SessionId}\tmsg={m}\tpayload={p}";
    }
}
