using OpenWorkmate.Server;
using OpenWorkmate.Server.Services.ScheduledTask;

namespace OpenWorkmate.Server.Services;

/// <summary>定时任务调度器：周期扫描任务目录，到点将任务 MD 发给 AI 执行，并更新 nextRunAt/lastRunAt。</summary>
public sealed class ScheduledTaskRunnerService : IHostedService, IDisposable
{
    private readonly IScheduledTaskStore _store;
    private readonly ChatService _chatService;
    private readonly ILogger<ScheduledTaskRunnerService> _logger;
    private System.Threading.Timer? _timer;
    private const int IntervalSeconds = 60;

    public ScheduledTaskRunnerService(
        IScheduledTaskStore store,
        ChatService chatService,
        ILogger<ScheduledTaskRunnerService> logger)
    {
        _store = store;
        _chatService = chatService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new System.Threading.Timer(RunScheduledTasksTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(IntervalSeconds));
        _logger.LogInformation("ScheduledTaskRunnerService started, interval={Sec}s", IntervalSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void RunScheduledTasksTick(object? _) => _ = RunScheduledTasksAsync();

    private async Task RunScheduledTasksAsync()
    {
        try
        {
            var list = await _store.ListAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var meta in list)
            {
                if (!meta.Enabled || meta.NextRunAt == null || meta.NextRunAt.Value > now)
                    continue;
                await RunOneTaskAsync(meta.Id).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScheduledTaskRunner tick failed");
        }
    }

    private async Task RunOneTaskAsync(string taskId)
    {
        var result = await _store.GetAsync(taskId).ConfigureAwait(false);
        if (result == null) return;
        var (content, meta) = result.Value;
        var sessionId = "scheduled:" + taskId;
        var userMessage = "[定时任务] 请按以下描述执行：\n\n" + content;
        try
        {
            await foreach (var _ in _chatService.StreamChatAsync(sessionId, userMessage))
            {
                // consume stream to completion
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task execution failed: {TaskId}", taskId);
        }

        meta.LastRunAt = DateTimeOffset.UtcNow;
        meta.RunCount = meta.RunCount + 1;

        if (string.Equals(meta.ScheduleType, "once", StringComparison.OrdinalIgnoreCase))
        {
            await _store.DeleteAsync(taskId).ConfigureAwait(false);
            _logger.LogInformation("Scheduled task deleted after run (once): {TaskId}", taskId);
            return;
        }

        if (meta.MaxRuns.HasValue && meta.RunCount >= meta.MaxRuns)
            meta.Enabled = false;
        if (meta.EndAt.HasValue && meta.LastRunAt >= meta.EndAt)
            meta.Enabled = false;

        var nextRun = CronNextRun.GetNextRunAt(meta, meta.LastRunAt.Value);
        meta.NextRunAt = nextRun;

        if (string.Equals(meta.ScheduleType, "interval", StringComparison.OrdinalIgnoreCase) && nextRun == null)
        {
            if (meta.IntervalSeconds.HasValue && meta.IntervalSeconds.Value > 0)
                meta.NextRunAt = meta.LastRunAt.Value.AddSeconds(meta.IntervalSeconds.Value);
            else
                meta.NextRunAt = meta.LastRunAt.Value.AddMinutes(meta.IntervalMinutes ?? 60);
        }

        if (meta.DeleteAfterRun)
        {
            await _store.DeleteAsync(taskId).ConfigureAwait(false);
            _logger.LogInformation("Scheduled task deleted after run (deleteAfterRun): {TaskId}", taskId);
            return;
        }

        if (!meta.Enabled)
        {
            await _store.UpdateMetaAsync(taskId, meta).ConfigureAwait(false);
            return;
        }

        await _store.UpdateMetaAsync(taskId, meta).ConfigureAwait(false);
        _logger.LogInformation("Scheduled task executed: {TaskId}, nextRunAt={Next}", taskId, meta.NextRunAt);
    }
}
