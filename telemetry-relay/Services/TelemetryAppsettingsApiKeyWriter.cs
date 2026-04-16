using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Taskly.Telemetry.Relay.Services;

public static class TelemetryAppsettingsApiKeyWriter
{
    /// <summary>URL-safe-ish random string (43 chars), suitable for Bearer / X-Telemetry-Key.</summary>
    public static string GenerateApiKey()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static async Task WriteTelemetryApiKeyAsync(string appsettingsPath, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appsettingsPath))
            throw new ArgumentException("appsettingsPath required.", nameof(appsettingsPath));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("apiKey required.", nameof(apiKey));

        var text = await File.ReadAllTextAsync(appsettingsPath, ct).ConfigureAwait(false);

        var root = JsonNode.Parse(text) ?? throw new InvalidOperationException("appsettings.json 为空或无法解析。");
        var telemetry = root["Telemetry"] as JsonObject;
        if (telemetry is null)
        {
            telemetry = new JsonObject();
            root["Telemetry"] = telemetry;
        }

        telemetry["ApiKey"] = apiKey;

        var outText = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(appsettingsPath, outText, ct).ConfigureAwait(false);
    }
}
