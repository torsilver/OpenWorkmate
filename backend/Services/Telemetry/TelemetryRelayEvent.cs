using System.Text.Json;

namespace OpenWorkmate.Server.Services.Telemetry;

public sealed class TelemetryRelayEvent
{
    public required string DeviceId { get; init; }
    public required string ClientTier { get; init; }
    public required string SessionId { get; init; }
    public required string EventType { get; init; }
    public string DetailLevel { get; init; } = "p0";
    public string? ClientType { get; init; }
    public string? ModelId { get; init; }
    public string? Message { get; init; }

    /// <summary>可选结构化字段（camelCase JSON），写入 Seq 时序列化为 TelemetryPayloadJson。</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>事件发生时间（UTC）；未设置时中继使用服务端收到批次的时刻。</summary>
    public DateTime? TimestampUtc { get; init; }
}

public interface ITelemetryRelayQueue
{
    void TryEnqueue(TelemetryRelayEvent ev);
}
