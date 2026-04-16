using System.Text.Json;
using Microsoft.Extensions.Options;
using Taskly.Telemetry.Relay.Models;

namespace Taskly.Telemetry.Relay.Services;

public sealed class TelemetryPolicyResolver
{
    private readonly IOptionsMonitor<TelemetryOptions> _opt;
    private readonly ILogger<TelemetryPolicyResolver> _logger;
    private readonly ConcurrentRefreshedCache<string, (TelemetryOverrideFile? Override, TelemetryDefaultsFile? Defaults, DateTime LoadedUtc)> _cache;

    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public TelemetryPolicyResolver(IOptionsMonitor<TelemetryOptions> opt, ILogger<TelemetryPolicyResolver> logger)
    {
        _opt = opt;
        _logger = logger;
        _cache = new ConcurrentRefreshedCache<string, (TelemetryOverrideFile?, TelemetryDefaultsFile?, DateTime)>(
            TimeSpan.FromSeconds(Math.Max(5, opt.CurrentValue.PolicyCacheSeconds)),
            LoadPair);
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

    public TelemetryDefaultsFile? ReadDefaultsOnly()
    {
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var path = Path.Combine(root, "telemetry-defaults.json");
        return ReadJson<TelemetryDefaultsFile>(path);
    }

    public void WriteDefaults(TelemetryDefaultsFile data)
    {
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "telemetry-defaults.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonWriteIndented));
        _cache.InvalidateAll();
    }

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

    private (TelemetryOverrideFile?, TelemetryDefaultsFile?, DateTime) LoadPair(string deviceId)
    {
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var defPath = Path.Combine(root, "telemetry-defaults.json");
        var ovPath = Path.Combine(root, "devices", deviceId, "telemetry-override.json");
        var def = ReadJson<TelemetryDefaultsFile>(defPath);
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
