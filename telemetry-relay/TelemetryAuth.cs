namespace Taskly.Telemetry.Relay;

public static class TelemetryAuth
{
    public static bool ValidatePolicyApiKey(HttpContext http, IConfiguration cfg)
    {
        var expected = (cfg.GetSection(TelemetryOptions.SectionName)["ApiKey"] ?? "").Trim();
        if (string.IsNullOrEmpty(expected))
            return false;
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var t = auth["Bearer ".Length..].Trim();
            if (t == expected) return true;
        }

        var header = (http.Request.Headers["X-Telemetry-Key"].FirstOrDefault() ?? "").Trim();
        return header == expected;
    }
}
