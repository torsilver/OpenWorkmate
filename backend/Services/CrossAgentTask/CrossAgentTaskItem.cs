namespace OfficeCopilot.Server.Services.CrossAgentTask;

/// <summary>跨 Agent 任务：由一端发起、指定目标端（clientType 或 sessionId）执行。</summary>
public sealed class CrossAgentTaskItem
{
    public string Id { get; set; } = "";
    /// <summary>发起方 sessionId。</summary>
    public string FromSessionId { get; set; } = "";
    /// <summary>目标端类型，如 office-word、chrome；与 TargetSessionId 二选一或同时用（先按 session 精确匹配）。</summary>
    public string? TargetClientType { get; set; }
    /// <summary>目标 sessionId（可选）；若指定则仅该 session 拉取。</summary>
    public string? TargetSessionId { get; set; }
    /// <summary>任务描述（用户或模型填写的“让 XX 做 X”中的 X）。</summary>
    public string Description { get; set; } = "";
    /// <summary>pending | done | failed。</summary>
    public string Status { get; set; } = "pending";
    /// <summary>执行完成后可写入的结果摘要。</summary>
    public string? ResultSummary { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
