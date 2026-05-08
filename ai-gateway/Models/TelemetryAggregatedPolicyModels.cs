using System.Text.Json.Serialization;

namespace OpenWorkmate.AI.Gateway.Models;

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

    /// <summary>本 Gateway/API Key 下允许客户端选择上报的 AI 流事件种类（与 <c>TelemetryRelayEvent.EventType</c> 对齐）。</summary>
    [JsonPropertyName("availableEventKinds")]
    public List<TelemetryEventKindEntry> AvailableEventKinds { get; set; } = new();

    /// <summary>管理员定义的多个策略配置（每组为启用的事件种类列表）。</summary>
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

    /// <summary>当前 profile 对 detail 级别（p0/p1/p2）的上限，与事件种类并列；与客户端会话参数取更严一侧。</summary>
    [JsonPropertyName("ingestLogLevel")]
    public string IngestLogLevel { get; set; } = "information";

    /// <summary><c>gateway</c> | <c>direct</c>；AI 后台据此选择 LLM 路由。</summary>
    [JsonPropertyName("routeMode")]
    public string RouteMode { get; set; } = "gateway";
}

/// <summary><c>GET /api/policy/aggregated</c> 完整响应（ops + user + effective）。</summary>
public sealed class AggregatedPolicyEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("syncedAt")]
    public DateTime SyncedAt { get; set; }

    [JsonPropertyName("etag")]
    public string ETag { get; set; } = "";

    [JsonPropertyName("ops")]
    public OpsPolicyBundle Ops { get; set; } = new();

    [JsonPropertyName("user")]
    public UserPolicyFile User { get; set; } = new();

    [JsonPropertyName("userOverlayViolations")]
    public List<string> UserOverlayViolations { get; set; } = new();

    /// <summary>与历史 AI 后台字段对齐的有效策略（= 原 aggregated body + routeMode）。</summary>
    [JsonPropertyName("effective")]
    public TelemetryAggregatedPolicyResponse Effective { get; set; } = new();
}
