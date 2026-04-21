using System.Text.Json;
using Microsoft.Extensions.Options;
using Taskly.AI.Gateway.Models;

namespace Taskly.AI.Gateway.Services;

public sealed class TelemetryPolicyResolver
{
    public const string BundleFileName = "policy.ops.json";

    private readonly IOptionsMonitor<AiGatewayOptions> _opt;
    private readonly ILogger<TelemetryPolicyResolver> _logger;
    private readonly ConcurrentRefreshedCache<string, (TelemetryOverrideFile? Override, TelemetryDefaultsFile? Defaults, DateTime LoadedUtc)> _cache;
    private readonly ConcurrentRefreshedCache<string, (TelemetryTransmissionPolicyFile Tx, TelemetryDefaultsFile? Def, TelemetryPolicyProfilesFile Prof, IReadOnlyList<string> AllowedRouteModes)> _bundleSnapshotCache;

    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public TelemetryPolicyResolver(IOptionsMonitor<AiGatewayOptions> opt, ILogger<TelemetryPolicyResolver> logger)
    {
        _opt = opt;
        _logger = logger;
        var ttl = TimeSpan.FromSeconds(Math.Max(5, opt.CurrentValue.PolicyCacheSeconds));
        _cache = new ConcurrentRefreshedCache<string, (TelemetryOverrideFile?, TelemetryDefaultsFile?, DateTime)>(
            ttl,
            LoadPairImpl);
        _bundleSnapshotCache = new ConcurrentRefreshedCache<string, (TelemetryTransmissionPolicyFile, TelemetryDefaultsFile?, TelemetryPolicyProfilesFile, IReadOnlyList<string>)>(
            ttl,
            _ => LoadBundleSnapshot());
    }

    /// <summary>与 bundle 内 <c>transmission</c> 合并后的策略；供 GET /policy/transmission 与 /policy/aggregated。</summary>
    public TelemetryTransmissionPolicyFile GetTransmissionPolicy() =>
        _bundleSnapshotCache.GetOrAdd("_").Tx;

    /// <summary>与 bundle 内 <c>policyProfiles</c> 合并后的多配置策略。</summary>
    public TelemetryPolicyProfilesFile GetPolicyProfiles() =>
        _bundleSnapshotCache.GetOrAdd("_").Prof;

    public IReadOnlyList<string> GetAllowedRouteModes() =>
        _bundleSnapshotCache.GetOrAdd("_").AllowedRouteModes;

    /// <summary>合并后的策略快照，供 Admin GET 展示（与运行时一致）。</summary>
    public OpsPolicyBundle GetBundleForDisplay()
    {
        var (tx, def, prof, allowedModes) = _bundleSnapshotCache.GetOrAdd("_");
        return new OpsPolicyBundle
        {
            SchemaVersion = 1,
            AllowedRouteModes = allowedModes.ToList(),
            Transmission = tx,
            Defaults = def,
            PolicyProfiles = prof
        };
    }

    /// <summary>写入完整 bundle（PUT /api/admin/policy）；会对 transmission / policyProfiles 做与缺省合并。</summary>
    public void WriteFullBundle(OpsPolicyBundle incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, BundleFileName);
        var modes = incoming.AllowedRouteModes is { Count: > 0 }
            ? incoming.AllowedRouteModes.Select(s => (s ?? "").Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList()
            : new List<string> { "gateway", "direct" };
        if (modes.Count == 0) modes.Add("gateway");
        var merged = new OpsPolicyBundle
        {
            SchemaVersion = incoming.SchemaVersion > 0 ? incoming.SchemaVersion : 1,
            AllowedRouteModes = modes,
            Transmission = TelemetryTransmissionPolicyDefaults.Merge(incoming.Transmission),
            Defaults = incoming.Defaults,
            PolicyProfiles = TelemetryPolicyProfilesDefaults.Merge(incoming.PolicyProfiles)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(merged, JsonWriteIndented));
        InvalidateBundleCaches();
    }

    public void InvalidateBundleCaches()
    {
        _bundleSnapshotCache.Invalidate("_");
        _cache.InvalidateAll();
    }

    public EffectivePolicy ResolveForDevice(string deviceId, string? clientTierString)
    {
        var client = TelemetryTierParser.ParseOrMinimal(clientTierString);
        var (ov, def, _) = _cache.GetOrAdd(deviceId);
        var effective = ResolveEffectiveTier(client, ov, def);
        var p2Rate = ov?.P2BodySampleRate ?? def?.DefaultP2BodySampleRate ?? 1.0;
        p2Rate = Math.Clamp(p2Rate, 0, 1);
        var sampleRate = Math.Clamp(ov?.SampleRate ?? 1.0, 0, 1);
        return new EffectivePolicy(effective, client, p2Rate, sampleRate);
    }

    public TelemetryDefaultsFile? ReadDefaultsOnly() =>
        _bundleSnapshotCache.GetOrAdd("_").Def;

    public TelemetryOverrideFile? ReadOverride(string deviceId)
    {
        if (!TelemetryPathValidator.IsValidDeviceId(deviceId)) return null;
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var path = Path.Combine(root, "devices", deviceId, "telemetry-override.json");
        return ReadJson<TelemetryOverrideFile>(path);
    }

    public void WriteOverride(string deviceId, TelemetryOverrideFile data)
    {
        if (!TelemetryPathValidator.IsValidDeviceId(deviceId))
            throw new ArgumentException("Invalid deviceId", nameof(deviceId));
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var dir = Path.Combine(root, "devices", deviceId);
        Directory.CreateDirectory(dir);
        data.UpdatedAt = DateTime.UtcNow;
        var path = Path.Combine(dir, "telemetry-override.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonWriteIndented));
        _cache.Invalidate(deviceId);
    }

    public void DeleteOverride(string deviceId)
    {
        if (!TelemetryPathValidator.IsValidDeviceId(deviceId)) return;
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var path = Path.Combine(root, "devices", deviceId, "telemetry-override.json");
        if (File.Exists(path)) File.Delete(path);
        _cache.Invalidate(deviceId);
    }

    private (TelemetryTransmissionPolicyFile Tx, TelemetryDefaultsFile? Def, TelemetryPolicyProfilesFile Prof, IReadOnlyList<string> AllowedRouteModes) LoadBundleSnapshot()
    {
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var path = Path.Combine(root, BundleFileName);
        var raw = ReadJson<OpsPolicyBundle>(path);
        var tx = TelemetryTransmissionPolicyDefaults.Merge(raw?.Transmission);
        var prof = TelemetryPolicyProfilesDefaults.Merge(raw?.PolicyProfiles);
        var modes = NormalizeAllowedRouteModes(raw?.AllowedRouteModes);
        return (tx, raw?.Defaults, prof, modes);
    }

    private static IReadOnlyList<string> NormalizeAllowedRouteModes(List<string>? fromFile)
    {
        if (fromFile is not { Count: > 0 })
            return new[] { "gateway", "direct" };
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in fromFile)
        {
            var t = (s ?? "").Trim().ToLowerInvariant();
            if (t is "gateway" or "direct") set.Add(t);
        }
        if (set.Count == 0) set.Add("gateway");
        return set.OrderBy(s => s == "direct" ? 1 : 0).ToList();
    }

    private (TelemetryOverrideFile?, TelemetryDefaultsFile?, DateTime) LoadPairImpl(string deviceId)
    {
        var (_, def, _, _) = _bundleSnapshotCache.GetOrAdd("_");
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var ovPath = Path.Combine(root, "devices", deviceId, "telemetry-override.json");
        var ov = ReadJson<TelemetryOverrideFile>(ovPath);
        return (ov, def, DateTime.UtcNow);
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonRead);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonWriteIndented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static TelemetryTier ResolveEffectiveTier(
        TelemetryTier client,
        TelemetryOverrideFile? ov,
        TelemetryDefaultsFile? def)
    {
        if (client == TelemetryTier.Off) return TelemetryTier.Off;
        if (ov?.ForceTier is { } f
            && TelemetryTierParser.TryParse(f, out var ft)
            && ft != TelemetryTier.Off)
            return ft;
        var capStr = ov?.EffectiveTierCap ?? def?.DefaultEffectiveTierCap;
        var cap = string.IsNullOrWhiteSpace(capStr)
            ? TelemetryTier.Full
            : TelemetryTierParser.TryParse(capStr, out var c)
                ? c
                : TelemetryTier.Full;
        return (TelemetryTier)Math.Min((int)client, (int)cap);
    }
}

public readonly record struct EffectivePolicy(
    TelemetryTier EffectiveTier,
    TelemetryTier ClientTier,
    double P2BodySampleRate,
    double EventSampleRate);


/// <summary>Per-key TTL cache with manual invalidate.</summary>
internal sealed class ConcurrentRefreshedCache<TKey, TValue> where TKey : notnull
{
    private readonly TimeSpan _ttl;
    private readonly Func<TKey, TValue> _loader;
    private readonly Dictionary<TKey, (TValue Value, DateTime ExpireUtc)> _map = new();
    private readonly object _lock = new();

    public ConcurrentRefreshedCache(TimeSpan ttl, Func<TKey, TValue> loader)
    {
        _ttl = ttl;
        _loader = loader;
    }

    public TValue GetOrAdd(TKey key)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_map.TryGetValue(key, out var e) && e.ExpireUtc > now)
                return e.Value;
            var v = _loader(key);
            _map[key] = (v, now + _ttl);
            return v;
        }
    }

    public void Invalidate(TKey key)
    {
        lock (_lock) { _map.Remove(key); }
    }

    public void InvalidateAll()
    {
        lock (_lock) { _map.Clear(); }
    }
}
