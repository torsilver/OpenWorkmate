using Microsoft.Extensions.Options;

namespace Taskly.Telemetry.Relay.Services;

/// <summary>Deletes session .txt files older than <see cref="TelemetryOptions.RetentionDays"/>.</summary>
public sealed class TelemetryRetentionBackgroundService : BackgroundService
{
    private readonly IOptionsMonitor<TelemetryOptions> _opt;
    private readonly ILogger<TelemetryRetentionBackgroundService> _logger;

    public TelemetryRetentionBackgroundService(
        IOptionsMonitor<TelemetryOptions> opt,
        ILogger<TelemetryRetentionBackgroundService> logger)
    {
        _opt = opt;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunSweep();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telemetry retention sweep failed");
            }

            var hours = Math.Clamp(_opt.CurrentValue.RetentionSweepHours, 1, 168);
            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken).ConfigureAwait(false);
        }
    }

    private void RunSweep()
    {
        var opt = _opt.CurrentValue;
        var root = Path.GetFullPath(opt.DataRoot);
        var devicesRoot = Path.Combine(root, "devices");
        if (!Directory.Exists(devicesRoot)) return;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, opt.RetentionDays));
        foreach (var deviceDir in Directory.GetDirectories(devicesRoot))
        {
            var sessions = Path.Combine(deviceDir, "sessions");
            if (!Directory.Exists(sessions)) continue;
            foreach (var file in Directory.GetFiles(sessions, "*.txt"))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc < cutoff)
                {
                    File.Delete(file);
                    _logger.LogInformation("Retention deleted {File}", file);
                }
            }
        }
    }
}
