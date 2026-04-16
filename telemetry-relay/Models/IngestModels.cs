using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taskly.Telemetry.Relay.Models;

public sealed class IngestBatchRequest
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>Client-reported tier for this batch (may be capped by relay policy).</summary>
    [JsonPropertyName("clientTier")]
    public string? ClientTier { get; set; }

    [JsonPropertyName("events")]
    public List<IngestEvent>? Events { get; set; }
}

public sealed class IngestEvent
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("timestampUtc")]
    public DateTime? TimestampUtc { get; set; }

    /// <summary>P0–P2 hint from producer; relay may drop P2 by policy.</summary>
    [JsonPropertyName("detailLevel")]
    public string? DetailLevel { get; set; }

    [JsonPropertyName("clientType")]
    public string? ClientType { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

public sealed class TelemetryDefaultsFile
{
    [JsonPropertyName("retentionDays")]
    public int? RetentionDays { get; set; }

    [JsonPropertyName("defaultP2BodySampleRate")]
    public double? DefaultP2BodySampleRate { get; set; } = 1.0;

    [JsonPropertyName("defaultEffectiveTierCap")]
    public string? DefaultEffectiveTierCap { get; set; }
}

public sealed class TelemetryOverrideFile
{
    [JsonPropertyName("sampleRate")]
    public double? SampleRate { get; set; } = 1.0;

    [JsonPropertyName("p2BodySampleRate")]
    public double? P2BodySampleRate { get; set; }

    [JsonPropertyName("effectiveTierCap")]
    public string? EffectiveTierCap { get; set; }

    [JsonPropertyName("forceTier")]
    public string? ForceTier { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}
