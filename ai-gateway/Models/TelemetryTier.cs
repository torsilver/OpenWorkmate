namespace OpenWorkmate.AI.Gateway.Models;

public enum TelemetryTier
{
    Off = 0,
    Minimal = 1,
    Traces = 2,
    Full = 3
}

public static class TelemetryTierParser
{
    public static bool TryParse(string? s, out TelemetryTier tier)
    {
        tier = TelemetryTier.Minimal;
        if (string.IsNullOrWhiteSpace(s)) return false;
        switch (s.Trim().ToLowerInvariant())
        {
            case "off": tier = TelemetryTier.Off; return true;
            case "minimal": tier = TelemetryTier.Minimal; return true;
            case "traces": tier = TelemetryTier.Traces; return true;
            case "full": tier = TelemetryTier.Full; return true;
            default: return false;
        }
    }

    public static TelemetryTier ParseOrMinimal(string? s) =>
        TryParse(s, out var t) ? t : TelemetryTier.Minimal;

    public static string ToApiString(TelemetryTier t) => t switch
    {
        TelemetryTier.Off => "off",
        TelemetryTier.Minimal => "minimal",
        TelemetryTier.Traces => "traces",
        TelemetryTier.Full => "full",
        _ => "minimal"
    };
}
