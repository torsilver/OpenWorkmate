namespace OfficeCopilot.Server.Services.ScheduledTask;

/// <summary>定时任务存储：基于文件的 .task.md + .meta.json CRUD。</summary>
public interface IScheduledTaskStore
{
    Task<IReadOnlyList<ScheduledTaskMeta>> ListAsync(CancellationToken ct = default);
    Task<(string Content, ScheduledTaskMeta Meta)?> GetAsync(string taskId, CancellationToken ct = default);
    Task<string> SaveAsync(string? taskId, string content, ScheduledTaskMeta? meta = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string taskId, CancellationToken ct = default);
    /// <summary>更新 meta（如执行后更新 lastRunAt、nextRunAt、runCount）。</summary>
    Task UpdateMetaAsync(string taskId, ScheduledTaskMeta meta, CancellationToken ct = default);
}
