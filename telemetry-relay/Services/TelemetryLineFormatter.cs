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
        TelemetryTransmissionPolicyFile transmission,
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
            TelemetryTier.Minimal => FormatMinimal(ts, type, ev, msg, transmission.Minimal),
            TelemetryTier.Traces => FormatTraces(ts, type, ev, msg, payloadStr, transmission.Traces, maxChars),
            TelemetryTier.Full => FormatFull(ts, type, ev, msg, payloadStr, transmission.Full, maxChars),
            _ => ""
        };
    }

    private static string FormatMinimal(string ts, string type, IngestEvent ev, string msg, MinimalTransmissionLimits lim)
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

            var preview = TelemetryPathValidator.SanitizeForLog(msg, lim.AssistantTurnFinalMsgPreviewMax);
            return $"{ts}\t{type}\tclientType={ct}\tmodelId={mid}\tcharCount={charCount}\ttruncated={truncated}\tmsgPreview={preview}\tsession={ev.SessionId}";
        }

        if (string.Equals(type, "tool_invocation_end", StringComparison.Ordinal))
        {
            var preview = TelemetryPathValidator.SanitizeForLog(msg, lim.ToolInvocationEndMsgPreviewMax);
            return $"{ts}\t{type}\tclientType={ct}\tmodelId={mid}\tmsgLen={msg.Length}\tmsgPreview={preview}\tsession={ev.SessionId}";
        }

        var len = msg.Length;
        return $"{ts}\t{type}\tclientType={ct}\tmodelId={mid}\tmsgLen={len}\tsession={ev.SessionId}";
    }

    private static string FormatTraces(string ts, string type, IngestEvent ev, string msg, string payload, TracesTransmissionLimits lim, int max)
    {
        var msgCap = Math.Min(lim.MsgMax, max);
        var payCap = Math.Min(lim.PayloadMax, max);
        var m = TelemetryPathValidator.SanitizeForLog(msg, msgCap);
        var p = payload.Length == 0 ? "" : TelemetryPathValidator.SanitizeForLog(payload, payCap);
        return $"{ts}\t{type}\tsession={ev.SessionId}\tmsg={m}\tpayload={p}";
    }

    private static string FormatFull(string ts, string type, IngestEvent ev, string msg, string payload, FullTransmissionLimits lim, int maxFromOptions)
    {
        var msgCap = Math.Min(lim.MsgMax, maxFromOptions);
        var payCap = Math.Min(lim.PayloadMax, maxFromOptions);
        var m = TelemetryPathValidator.SanitizeForLog(msg, msgCap);
        var p = payload.Length == 0 ? "" : TelemetryPathValidator.SanitizeForLog(payload, payCap);
        return $"{ts}\t{type}\tsession={ev.SessionId}\tmsg={m}\tpayload={p}";
    }
}
