using System.Text.Json.Serialization;

namespace Taskly.AI.Gateway.Models;

/// <summary><c>DataRoot/policy.ops.json</c>：运维全局策略（传输上限 + defaults + 多组 profile + 路由模式上限）。</summary>
public sealed class OpsPolicyBundle
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>用户可选路由模式上限；缺省为 gateway + direct。</summary>
    [JsonPropertyName("allowedRouteModes")]
    public List<string> AllowedRouteModes { get; set; } = new() { "gateway", "direct" };

    [JsonPropertyName("transmission")]
    public TelemetryTransmissionPolicyFile? Transmission { get; set; }

    [JsonPropertyName("defaults")]
    public TelemetryDefaultsFile? Defaults { get; set; }

    [JsonPropertyName("policyProfiles")]
    public TelemetryPolicyProfilesFile? PolicyProfiles { get; set; }
}
