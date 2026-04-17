using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>与遥测中继 <c>GET /policy/aggregated</c> 对齐（camelCase）。</summary>
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

    [JsonPropertyName("availableLogKinds")]
    public List<TelemetryAggregatedLogKindEntry>? AvailableLogKinds { get; set; }

    [JsonPropertyName("policyProfiles")]
    public List<TelemetryAggregatedPolicyProfileEntry>? PolicyProfiles { get; set; }

    [JsonPropertyName("defaultPolicyProfileId")]
    public string? DefaultPolicyProfileId { get; set; }

    [JsonPropertyName("selectedPolicyProfileId")]
    public string? SelectedPolicyProfileId { get; set; }

    /// <summary>为 <c>false</c> 时 AI 端视为策略不健康，不向 Seq 发送结构化遥测；缺省按 <c>true</c> 反序列化。</summary>
    [JsonPropertyName("telemetryEmissionAllowed")]
    public bool? TelemetryEmissionAllowed { get; set; }

    /// <summary>当前 profile 的 detail 级别上限（与遥测中继 <c>ingestLogLevel</c> 一致）；与 WebSocket 会话参数取更严一侧。</summary>
    [JsonPropertyName("ingestLogLevel")]
    public string? IngestLogLevel { get; set; }

    [JsonPropertyName("maxEventPayloadChars")]
    public int MaxEventPayloadChars { get; set; }
}

public sealed class TelemetryAggregatedLogKindEntry
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

    [JsonPropertyName("logKinds")]
    public List<TelemetryAggregatedLogKindEntry> LogKinds { get; set; } = new();

    [JsonPropertyName("ingestLogLevel")]
    public string? IngestLogLevel { get; set; }

    [JsonPropertyName("transmission")]
    public TelemetryTransmissionPolicyFile? Transmission { get; set; }
}
