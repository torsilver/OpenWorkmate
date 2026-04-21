using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>与 AI Gateway 聚合策略 <c>effective</c> 对象对齐（camelCase）。</summary>
public sealed class TelemetryAggregatedPolicyResponse
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("syncedAt")]
    public DateTime SyncedAt { get; set; }

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("transmission")]
    public TelemetryTransmissionPolicyFile? Transmission { get; set; }

    [JsonPropertyName("availableEventKinds")]
    public List<TelemetryAggregatedEventKindEntry>? AvailableEventKinds { get; set; }

    [JsonPropertyName("policyProfiles")]
    public List<TelemetryAggregatedPolicyProfileEntry>? PolicyProfiles { get; set; }

    [JsonPropertyName("defaultPolicyProfileId")]
    public string? DefaultPolicyProfileId { get; set; }

    [JsonPropertyName("selectedPolicyProfileId")]
    public string? SelectedPolicyProfileId { get; set; }

    /// <summary>为 <c>false</c> 时 AI 端视为策略不健康，不向 Gateway ingest 发送结构化遥测；缺省按 <c>true</c> 反序列化。</summary>
    [JsonPropertyName("telemetryEmissionAllowed")]
    public bool? TelemetryEmissionAllowed { get; set; }

    /// <summary>当前 profile 的 detail 级别上限（与 AI Gateway <c>ingestLogLevel</c> 一致）；与 WebSocket 会话参数取更严一侧。</summary>
    [JsonPropertyName("ingestLogLevel")]
    public string? IngestLogLevel { get; set; }

    [JsonPropertyName("maxEventPayloadChars")]
    public int MaxEventPayloadChars { get; set; }

    [JsonPropertyName("routeMode")]
    public string? RouteMode { get; set; }
}

/// <summary>AI Gateway <c>GET /api/policy/aggregated</c> 响应；后台只消费 <see cref="Effective"/>。</summary>
public sealed class AggregatedPolicyEnvelope
{
    [JsonPropertyName("effective")]
    public TelemetryAggregatedPolicyResponse? Effective { get; set; }
}

public sealed class TelemetryAggregatedEventKindEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public sealed class TelemetryAggregatedPolicyProfileEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("eventKinds")]
    public List<TelemetryAggregatedEventKindEntry> EventKinds { get; set; } = new();

    [JsonPropertyName("ingestLogLevel")]
    public string? IngestLogLevel { get; set; }

    [JsonPropertyName("transmission")]
    public TelemetryTransmissionPolicyFile? Transmission { get; set; }
}
