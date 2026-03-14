namespace OfficeCopilot.Server.Services.CrossAgentTask;

/// <summary>跨 Agent 任务存储。</summary>
public interface ICrossAgentTaskStore
{
    Task<CrossAgentTaskItem> AddAsync(string fromSessionId, string? targetClientType, string? targetSessionId, string description, CancellationToken ct = default);
    /// <summary>拉取指定目标下的待办（按 clientType 或 sessionId 匹配）。</summary>
    Task<IReadOnlyList<CrossAgentTaskItem>> GetPendingForTargetAsync(string? clientType, string? sessionId, CancellationToken ct = default);
    Task<CrossAgentTaskItem?> GetAsync(string id, CancellationToken ct = default);
    Task<bool> UpdateStatusAsync(string id, string status, string? resultSummary, CancellationToken ct = default);
}
