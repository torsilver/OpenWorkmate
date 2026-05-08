namespace OpenWorkmate.AI.Gateway.Services;

public static class TelemetryPathValidator
{
    public static bool IsValidDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var s = deviceId.Trim();
        return Guid.TryParse(s, out _);
    }

    /// <summary>Safe segment for directory/file under sessions/ (no path chars).</summary>
    public static bool IsValidSessionFileKey(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var s = sessionId.Trim();
        if (s.Length is < 1 or > 128) return false;
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                continue;
            return false;
        }

        return true;
    }

    public static string SanitizeForLog(string? s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= maxLen) return s;
        return s[..maxLen] + "…";
    }
}
