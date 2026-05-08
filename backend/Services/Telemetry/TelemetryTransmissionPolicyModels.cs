using System.Text.Json.Serialization;

namespace OpenWorkmate.Server.Services.Telemetry;

/// <summary>与遥测中继 <c>GET /policy/transmission</c> 及 <c>DataRoot/telemetry-relay-policy.json</c> 内 <c>transmission</c> 对齐（camelCase）。</summary>
public sealed class TelemetryTransmissionPolicyFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("minimal")]
    public MinimalTransmissionLimits Minimal { get; set; } = new();

    [JsonPropertyName("traces")]
    public TracesTransmissionLimits Traces { get; set; } = new();

    [JsonPropertyName("full")]
    public FullTransmissionLimits Full { get; set; } = new();
}

public sealed class MinimalTransmissionLimits
{
    [JsonPropertyName("assistantTurnFinalMsgPreviewMax")]
    public int AssistantTurnFinalMsgPreviewMax { get; set; } = 240;

    [JsonPropertyName("toolInvocationEndMsgPreviewMax")]
    public int ToolInvocationEndMsgPreviewMax { get; set; } = 120;

    [JsonPropertyName("otherEventMsgMax")]
    public int OtherEventMsgMax { get; set; } = 8192;
}

public sealed class TracesTransmissionLimits
{
    [JsonPropertyName("msgMax")]
    public int MsgMax { get; set; } = 500;

    [JsonPropertyName("payloadMax")]
    public int PayloadMax { get; set; } = 2000;
}

public sealed class FullTransmissionLimits
{
    [JsonPropertyName("msgMax")]
    public int MsgMax { get; set; } = 50_000;

    [JsonPropertyName("payloadMax")]
    public int PayloadMax { get; set; } = 50_000;
}

public static class TelemetryTransmissionPolicyDefaults
{
    public static TelemetryTransmissionPolicyFile CreateDefault() => new()
    {
        SchemaVersion = 1,
        Minimal = new MinimalTransmissionLimits(),
        Traces = new TracesTransmissionLimits(),
        Full = new FullTransmissionLimits()
    };

    public static TelemetryTransmissionPolicyFile Merge(TelemetryTransmissionPolicyFile? fromRemote)
    {
        var d = CreateDefault();
        if (fromRemote is null) return d;

        d.SchemaVersion = fromRemote.SchemaVersion > 0 ? fromRemote.SchemaVersion : d.SchemaVersion;

        d.Minimal.AssistantTurnFinalMsgPreviewMax = PosOrDefault(
            fromRemote.Minimal.AssistantTurnFinalMsgPreviewMax, d.Minimal.AssistantTurnFinalMsgPreviewMax);
        d.Minimal.ToolInvocationEndMsgPreviewMax = PosOrDefault(
            fromRemote.Minimal.ToolInvocationEndMsgPreviewMax, d.Minimal.ToolInvocationEndMsgPreviewMax);
        d.Minimal.OtherEventMsgMax = PosOrDefault(fromRemote.Minimal.OtherEventMsgMax, d.Minimal.OtherEventMsgMax);

        d.Traces.MsgMax = PosOrDefault(fromRemote.Traces.MsgMax, d.Traces.MsgMax);
        d.Traces.PayloadMax = PosOrDefault(fromRemote.Traces.PayloadMax, d.Traces.PayloadMax);

        d.Full.MsgMax = PosOrDefault(fromRemote.Full.MsgMax, d.Full.MsgMax);
        d.Full.PayloadMax = PosOrDefault(fromRemote.Full.PayloadMax, d.Full.PayloadMax);

        return d;
    }

    private static int PosOrDefault(int v, int def) => v > 0 ? v : def;
}
