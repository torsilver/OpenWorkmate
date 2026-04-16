using System.Text.Json;

namespace OfficeCopilot.Server.Services.Telemetry;

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

    /// <summary>可选结构化字段，与遥测中继 <c>IngestEvent.Payload</c> 对齐（camelCase JSON）。</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>事件发生时间（UTC）；未设置时中继使用服务端收到批次的时刻。</summary>
    public DateTime? TimestampUtc { get; init; }
}

public interface ITelemetryRelayQueue
{
    void TryEnqueue(TelemetryRelayEvent ev);
}
