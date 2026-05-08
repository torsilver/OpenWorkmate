using System.Text.Json.Serialization;

namespace OpenWorkmate.AI.Gateway.Models;

/// <summary><c>DataRoot/policy.user.json</c>：用户侧策略（由扩展 PUT /api/policy/user 写入）。</summary>
public sealed class UserPolicyFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary><c>gateway</c> | <c>direct</c></summary>
    [JsonPropertyName("routeMode")]
    public string RouteMode { get; set; } = "gateway";
}
