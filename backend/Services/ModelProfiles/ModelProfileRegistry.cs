using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server.Services.ModelProfiles;

/// <summary>加载 <c>ModelProfiles/vendor/model_prices_excerpt.json</c> 与 <c>open-workmate-overlay.json</c> 并合并。</summary>
public sealed class ModelProfileRegistry
{
    private readonly ILogger<ModelProfileRegistry>? _logger;
    private IReadOnlyDictionary<string, MergedModelProfile> _profiles =
        new Dictionary<string, MergedModelProfile>(StringComparer.OrdinalIgnoreCase);

    public ModelProfileRegistry(ILogger<ModelProfileRegistry>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>从应用基目录（输出目录）加载；失败时保留空表并打日志。</summary>
    public void Reload(string? baseDirectory = null)
    {
        var root = string.IsNullOrEmpty(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var vendorPath = Path.Combine(root, "ModelProfiles", "vendor", "model_prices_excerpt.json");
        var overlayPath = Path.Combine(root, "ModelProfiles", "open-workmate-overlay.json");

        if (!File.Exists(vendorPath))
        {
            _logger?.LogWarning("ModelProfiles vendor excerpt missing: {Path}", vendorPath);
            _profiles = new Dictionary<string, MergedModelProfile>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        JsonDocument? overlayDoc = null;
        try
        {
            if (File.Exists(overlayPath))
                overlayDoc = JsonDocument.Parse(File.ReadAllBytes(overlayPath));
        }
        catch
        {
            _logger?.LogWarning("ModelProfiles OpenWorkmate overlay invalid: {Path}", overlayPath);
        }

        var overlayRoot = overlayDoc?.RootElement ?? default;
        var overlayOk = overlayRoot.ValueKind == JsonValueKind.Object;

        var dict = new Dictionary<string, MergedModelProfile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var vendorDoc = JsonDocument.Parse(File.ReadAllBytes(vendorPath));
            var vendorRoot = vendorDoc.RootElement;
            if (vendorRoot.ValueKind != JsonValueKind.Object)
                throw new JsonException("vendor root not object");

            foreach (var prop in vendorRoot.EnumerateObject())
            {
                if (prop.Name.StartsWith("_", StringComparison.Ordinal))
                    continue;
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;

                JsonElement overlayForKey = default;
                var hasOverlay = overlayOk
                                 && overlayRoot.TryGetProperty(prop.Name, out overlayForKey)
                                 && overlayForKey.ValueKind == JsonValueKind.Object;

                dict[prop.Name] = MergeOne(prop.Name, prop.Value, hasOverlay ? overlayForKey : default);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ModelProfiles vendor excerpt invalid: {Path}", vendorPath);
            _profiles = new Dictionary<string, MergedModelProfile>(StringComparer.OrdinalIgnoreCase);
            overlayDoc?.Dispose();
            return;
        }

        overlayDoc?.Dispose();

        _profiles = dict;
        _logger?.LogInformation("ModelProfileRegistry loaded {Count} profiles from vendor excerpt.", dict.Count);
    }

    public bool TryGetMerged(string profileKey, out MergedModelProfile? profile)
    {
        if (_profiles.TryGetValue(profileKey, out var p))
        {
            profile = p;
            return true;
        }

        profile = null;
        return false;
    }

    public bool TryGetMergedForModelEntry(AiModelEntry? entry, out MergedModelProfile? profile)
    {
        profile = null;
        if (entry is null) return false;
        var key = (entry.ModelProfileKey ?? "").Trim();
        if (key.Length == 0) return false;
        return TryGetMerged(key, out profile);
    }

    private static MergedModelProfile MergeOne(string profileKey, JsonElement lite, JsonElement overlay)
    {
        static int? ReadInt(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var p)) return null;
            return p.ValueKind switch
            {
                JsonValueKind.Number when p.TryGetInt32(out var i) => i,
                JsonValueKind.String when int.TryParse(p.GetString(), out var j) => j,
                _ => null
            };
        }

        static bool ReadBool(JsonElement obj, string name, bool defaultValue = false)
        {
            if (!obj.TryGetProperty(name, out var p)) return defaultValue;
            return p.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue
            };
        }

        static bool? ReadNullableBool(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var p)) return null;
            return p.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => null
            };
        }

        static string? ReadString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
            return p.GetString();
        }

        var maxIn = ReadInt(lite, "max_input_tokens");
        var maxOut = ReadInt(lite, "max_output_tokens");
        if (maxOut is null) maxOut = ReadInt(lite, "max_tokens");

        var requiresEcho = overlay.ValueKind == JsonValueKind.Object && ReadBool(overlay, "requiresReasoningEchoWithTools");
        var suppressThinking = overlay.ValueKind == JsonValueKind.Object && ReadBool(overlay, "suppressUpstreamThinkingWithTools");
        var disableReasoningEcho = overlay.ValueKind == JsonValueKind.Object && ReadBool(overlay, "disableReasoningHttpEcho");
        var useThinkingKeepAll = overlay.ValueKind == JsonValueKind.Object && ReadBool(overlay, "useThinkingKeepAll");
        var recThink = overlay.ValueKind == JsonValueKind.Object ? ReadNullableBool(overlay, "recommendedEnableThinking") : null;
        var recBudget = overlay.ValueKind == JsonValueKind.Object ? ReadInt(overlay, "recommendedThinkingBudget") : null;
        var notes = overlay.ValueKind == JsonValueKind.Object ? ReadString(overlay, "notes") : null;

        return new MergedModelProfile
        {
            ProfileKey = profileKey,
            MaxInputTokens = maxIn,
            MaxOutputTokens = maxOut,
            SupportsFunctionCalling = ReadBool(lite, "supports_function_calling"),
            SupportsVision = ReadBool(lite, "supports_vision"),
            SupportsReasoning = ReadBool(lite, "supports_reasoning"),
            RequiresReasoningEchoWithTools = requiresEcho,
            SuppressUpstreamThinkingWithTools = suppressThinking,
            DisableReasoningHttpEcho = disableReasoningEcho,
            UseThinkingKeepAll = useThinkingKeepAll,
            RecommendedEnableThinking = recThink,
            RecommendedThinkingBudget = recBudget,
            Notes = notes
        };
    }
}
