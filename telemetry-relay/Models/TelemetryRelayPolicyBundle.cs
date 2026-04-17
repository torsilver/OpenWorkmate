using System.Text.Json.Serialization;

namespace Taskly.Telemetry.Relay.Models;

/// <summary>
/// <c>DataRoot/telemetry-relay-policy.json</c>：全局策略单文件（传输上限 + defaults + 多组 profile）。
/// 按设备覆写仍为 <c>devices/&lt;deviceId&gt;/telemetry-override.json</c>。
/// </summary>
public sealed class TelemetryRelayPolicyBundle
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("transmission")]
    public TelemetryTransmissionPolicyFile? Transmission { get; set; }

    [JsonPropertyName("defaults")]
    public TelemetryDefaultsFile? Defaults { get; set; }

    [JsonPropertyName("policyProfiles")]
    public TelemetryPolicyProfilesFile? PolicyProfiles { get; set; }
}
