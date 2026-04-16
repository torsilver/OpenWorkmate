using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Taskly.Telemetry.Relay.Models;

namespace Taskly.Telemetry.Relay.Services;

public sealed class TelemetrySessionWriter
{
    private readonly IOptionsMonitor<TelemetryOptions> _opt;
    private readonly ILogger<TelemetrySessionWriter> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public TelemetrySessionWriter(IOptionsMonitor<TelemetryOptions> opt, ILogger<TelemetrySessionWriter> logger)
    {
        _opt = opt;
        _logger = logger;
    }

    public async Task AppendLineAsync(string deviceId, string sessionId, string line, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(line)) return;
        var root = Path.GetFullPath(_opt.CurrentValue.DataRoot);
        var sessionsDir = Path.Combine(root, "devices", deviceId, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var path = Path.Combine(sessionsDir, sessionId + ".txt");
        EnsureDeviceMetaOnce(root, deviceId);

        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureDeviceMetaOnce(string dataRoot, string deviceId)
    {
        var deviceDir = Path.Combine(dataRoot, "devices", deviceId);
        var meta = Path.Combine(deviceDir, "device-meta.txt");
        if (File.Exists(meta)) return;
        try
        {
            Directory.CreateDirectory(deviceDir);
            if (File.Exists(meta)) return;
            File.WriteAllText(meta,
                $"firstSeenUtc={DateTime.UtcNow:O}{Environment.NewLine}deviceId={deviceId}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "device-meta skip {DeviceId}", deviceId);
        }
    }
}
