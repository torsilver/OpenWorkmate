using System.Text.Json.Serialization;

namespace OpenWorkmate.AI.Gateway.Models;

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
