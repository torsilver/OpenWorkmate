using System.Text.Json.Serialization;

namespace Taskly.Telemetry.Relay.Models;

/// <summary>落盘与 AI 出站共用的传输上限（<c>DataRoot/telemetry-relay-policy.json</c> 内 <c>transmission</c>，与缺省合并后由 GET /policy/transmission 等返回）。观测持久化在 Seq。</summary>
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
    /// <summary>assistant_turn_final 的 msg 出站上限（与落盘 msgPreview 一致）。</summary>
    [JsonPropertyName("assistantTurnFinalMsgPreviewMax")]
    public int AssistantTurnFinalMsgPreviewMax { get; set; } = 240;

    /// <summary>tool_invocation_end 的 msg 出站上限。</summary>
    [JsonPropertyName("toolInvocationEndMsgPreviewMax")]
    public int ToolInvocationEndMsgPreviewMax { get; set; } = 120;

    /// <summary>其余 eventType 在 minimal 档下的 msg 上限（落盘不含正文，仅限制链路与对齐 AI 出站）。</summary>
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
    /// <summary>与 <see cref="TelemetryOptions.MaxEventPayloadChars"/> 对齐时的默认；聚合策略响应会再与 Options 取 min。</summary>
    [JsonPropertyName("msgMax")]
    public int MsgMax { get; set; } = 50_000;

    [JsonPropertyName("payloadMax")]
    public int PayloadMax { get; set; } = 50_000;
}

/// <summary>文件缺失或字段为 0 时与代码默认合并。</summary>
public static class TelemetryTransmissionPolicyDefaults
{
    public static TelemetryTransmissionPolicyFile CreateDefault() => new()
    {
        SchemaVersion = 1,
        Minimal = new MinimalTransmissionLimits(),
        Traces = new TracesTransmissionLimits(),
        Full = new FullTransmissionLimits()
    };

    public static TelemetryTransmissionPolicyFile Merge(TelemetryTransmissionPolicyFile? fromFile)
    {
        var d = CreateDefault();
        if (fromFile is null) return d;

        d.SchemaVersion = fromFile.SchemaVersion > 0 ? fromFile.SchemaVersion : d.SchemaVersion;

        d.Minimal.AssistantTurnFinalMsgPreviewMax = PosOrDefault(
            fromFile.Minimal.AssistantTurnFinalMsgPreviewMax, d.Minimal.AssistantTurnFinalMsgPreviewMax);
        d.Minimal.ToolInvocationEndMsgPreviewMax = PosOrDefault(
            fromFile.Minimal.ToolInvocationEndMsgPreviewMax, d.Minimal.ToolInvocationEndMsgPreviewMax);
        d.Minimal.OtherEventMsgMax = PosOrDefault(fromFile.Minimal.OtherEventMsgMax, d.Minimal.OtherEventMsgMax);

        d.Traces.MsgMax = PosOrDefault(fromFile.Traces.MsgMax, d.Traces.MsgMax);
        d.Traces.PayloadMax = PosOrDefault(fromFile.Traces.PayloadMax, d.Traces.PayloadMax);

        d.Full.MsgMax = PosOrDefault(fromFile.Full.MsgMax, d.Full.MsgMax);
        d.Full.PayloadMax = PosOrDefault(fromFile.Full.PayloadMax, d.Full.PayloadMax);

        return d;
    }

    /// <summary>在已通过 <see cref="Merge"/> 得到的全局策略上叠加 profile 片段（仅当 overlay 中对应字段 &gt; 0 时覆盖）。</summary>
    public static TelemetryTransmissionPolicyFile MergeOverlay(TelemetryTransmissionPolicyFile globalMerged, TelemetryTransmissionPolicyFile? overlay)
    {
        var d = Merge(globalMerged);
        if (overlay is null) return d;
        var o = Merge(overlay);
        if (o.SchemaVersion > d.SchemaVersion) d.SchemaVersion = o.SchemaVersion;
        if (o.Minimal.AssistantTurnFinalMsgPreviewMax > 0) d.Minimal.AssistantTurnFinalMsgPreviewMax = o.Minimal.AssistantTurnFinalMsgPreviewMax;
        if (o.Minimal.ToolInvocationEndMsgPreviewMax > 0) d.Minimal.ToolInvocationEndMsgPreviewMax = o.Minimal.ToolInvocationEndMsgPreviewMax;
        if (o.Minimal.OtherEventMsgMax > 0) d.Minimal.OtherEventMsgMax = o.Minimal.OtherEventMsgMax;
        if (o.Traces.MsgMax > 0) d.Traces.MsgMax = o.Traces.MsgMax;
        if (o.Traces.PayloadMax > 0) d.Traces.PayloadMax = o.Traces.PayloadMax;
        if (o.Full.MsgMax > 0) d.Full.MsgMax = o.Full.MsgMax;
        if (o.Full.PayloadMax > 0) d.Full.PayloadMax = o.Full.PayloadMax;
        return d;
    }

    private static int PosOrDefault(int v, int def) => v > 0 ? v : def;
}
