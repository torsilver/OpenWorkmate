using System.Text.Json;

namespace OfficeCopilot.Server.Services.SkillVm;

public static class SkillVmManifestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static SkillVmManifest? TryLoad(string manifestPath, ILogger? logger = null)
    {
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var json = File.ReadAllText(manifestPath);
            var m = JsonSerializer.Deserialize<SkillVmManifest>(json, Options);
            if (m == null || m.Segments.Count == 0) return null;
            foreach (var s in m.Segments)
            {
                if (string.IsNullOrWhiteSpace(s.Id)) return null;
            }
            return m;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load skill manifest {Path}", manifestPath);
            return null;
        }
    }
}
