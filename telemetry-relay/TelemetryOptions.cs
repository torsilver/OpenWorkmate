namespace Taskly.Telemetry.Relay;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Shared secret for POST /ingest/batch (Bearer or X-Telemetry-Key).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Secret for /admin/* JSON APIs (optional; if empty, only localhost may call admin).</summary>
    public string? AdminApiKey { get; set; }

    public string DataRoot { get; set; } = "data";

    public int RetentionDays { get; set; } = 30;

    /// <summary>How often to run retention sweep.</summary>
    public int RetentionSweepHours { get; set; } = 24;

    public int PolicyCacheSeconds { get; set; } = 30;

    public int MaxEventPayloadChars { get; set; } = 50_000;
}
