namespace OfficeCopilot.Server.Services.Chat;

/// <summary>
/// HITL 工作流请求：工具调用需要用户确认时，由 Workflow RequestPort 发送给外部系统。
/// 设计为未来将 <see cref="HitlManager"/> 的 <c>TaskCompletionSource</c> 模式迁移到 MAF Workflow 级别暂停/恢复。
/// </summary>
public sealed record HitlWorkflowRequest(
    string RequestId,
    string SessionId,
    string PluginName,
    string FunctionName,
    string ActionDescription,
    string? HumanSummary,
    string? HitlKind,
    string? AddToAllowListKey);

/// <summary>HITL 工作流响应：用户确认或拒绝工具调用。</summary>
public sealed record HitlWorkflowResponse(
    bool Allowed,
    bool AddToAllowList);

/// <summary>
/// 用户选项工作流请求：需要用户在多步骤选项中做出选择。
/// 对应 <see cref="UserOptionsManager"/> 的 <c>ask_options_request</c>。
/// </summary>
public sealed record AskOptionsWorkflowRequest(
    string RequestId,
    string SessionId,
    string Title,
    string Prompt,
    IReadOnlyList<AskOptionsStep> Steps);

/// <summary>用户选项工作流响应。</summary>
public sealed record AskOptionsWorkflowResponse(
    bool TimedOut,
    IReadOnlyDictionary<string, string> Selections);
