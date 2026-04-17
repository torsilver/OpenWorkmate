using System.Text.Json.Serialization;

namespace Taskly.Telemetry.Relay.Models;

/// <summary>GET /policy/aggregated 返回体：传输上限 + defaults 摘要 + 同步元数据。</summary>
public sealed class TelemetryAggregatedPolicyResponse
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("syncedAt")]
    public DateTime SyncedAt { get; set; }

    [JsonPropertyName("etag")]
    public string ETag { get; set; } = "";

    [JsonPropertyName("transmission")]
    public TelemetryTransmissionPolicyFile Transmission { get; set; } = new();

    [JsonPropertyName("defaults")]
    public TelemetryDefaultsFile? Defaults { get; set; }

    [JsonPropertyName("maxEventPayloadChars")]
    public int MaxEventPayloadChars { get; set; }

    /// <summary>本中继/API Key 下允许客户端选择上报的 log 种类（与 <c>TelemetryRelayEvent.EventType</c> 对齐）。</summary>
    [JsonPropertyName("availableLogKinds")]
    public List<TelemetryLogKindEntry> AvailableLogKinds { get; set; } = new();

    /// <summary>管理员定义的多个策略配置（每组为启用的 log 种类列表）。</summary>
    [JsonPropertyName("policyProfiles")]
    public List<TelemetryPolicyProfileEntry> PolicyProfiles { get; set; } = new();

    /// <summary>默认选中的策略配置 Id（与 query <c>profileId</c> 缺省时一致）。</summary>
    [JsonPropertyName("defaultPolicyProfileId")]
    public string DefaultPolicyProfileId { get; set; } = "default";

    /// <summary>当前响应所依据的策略配置 Id（由 query <c>profileId</c> 或默认值解析）。</summary>
    [JsonPropertyName("selectedPolicyProfileId")]
    public string SelectedPolicyProfileId { get; set; } = "";

    /// <summary>为 <c>false</c> 时 AI 端视为策略不健康（fail-closed）；空列表亦可全停，此项为显式开关。</summary>
    [JsonPropertyName("telemetryEmissionAllowed")]
    public bool TelemetryEmissionAllowed { get; set; } = true;

    /// <summary>当前 profile 对 detail 级别（p0/p1/p2）的上限，与 log 种类并列；与客户端会话参数取更严一侧。</summary>
    [JsonPropertyName("ingestLogLevel")]
    public string IngestLogLevel { get; set; } = "information";
}
