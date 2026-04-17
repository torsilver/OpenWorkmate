namespace Taskly.Telemetry.Relay;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Shared secret for GET /policy/* (Bearer or X-Telemetry-Key)；与 AI 后台 user-config 一致。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Secret for /admin/* JSON APIs (optional; if empty, only localhost may call admin).</summary>
    public string? AdminApiKey { get; set; }

    public string DataRoot { get; set; } = "data";

    public int RetentionDays { get; set; } = 30;

    /// <summary>How often to run retention sweep.</summary>
    public int RetentionSweepHours { get; set; } = 24;

    public int PolicyCacheSeconds { get; set; } = 30;

    public int MaxEventPayloadChars { get; set; } = 50_000;

    /// <summary>非空时：中继进程自身 Serilog 写入此 Seq（可选）；用户观测由 AI 后台直写 Seq。</summary>
    public string? SeqServerUrl { get; set; }

    /// <summary>Seq API Key（私有部署可选）。</summary>
    public string? SeqApiKey { get; set; }
}
