using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server.Logging;

/// <summary>定期删除本地 Serilog 滚动日志（openworkmate-*.txt）中超过固定保留天数的文件。</summary>
public sealed class SerilogFileRetentionBackgroundService : BackgroundService
{
    private const int RetentionDays = 5;
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly ILogger<SerilogFileRetentionBackgroundService> _logger;

    public SerilogFileRetentionBackgroundService(ILogger<SerilogFileRetentionBackgroundService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = DebugLogHelper.DeleteRollingLogsOlderThanDays(RetentionDays);
                if (removed > 0)
                {
                    _logger.LogInformation(
                        "Serilog retention removed {Removed} file(s) older than {Days} days.",
                        removed,
                        RetentionDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Serilog retention run failed.");
            }

            try
            {
                await Task.Delay(RunInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
