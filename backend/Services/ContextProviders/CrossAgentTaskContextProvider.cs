using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services.CrossAgentTask;

namespace OfficeCopilot.Server.Services.ContextProviders;

/// <summary>
/// MAF <see cref="MessageAIContextProvider"/>：检索跨端待办并注入为额外 system 消息。
/// 每轮创建新实例（捕获 turn 级参数）。
/// </summary>
internal sealed class CrossAgentTaskContextProvider : MessageAIContextProvider
{
    private readonly ICrossAgentTaskStore _taskStore;
    private readonly SessionManager _sessionManager;
    private readonly string _sessionId;
    private readonly ILogger _logger;

    public CrossAgentTaskContextProvider(
        ICrossAgentTaskStore taskStore,
        SessionManager sessionManager,
        string sessionId,
        ILogger logger)
    {
        _taskStore = taskStore;
        _sessionManager = sessionManager;
        _sessionId = sessionId;
        _logger = logger;
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var clientType = _sessionManager.GetClientType(_sessionId);
            var pending = await _taskStore.GetPendingForTargetAsync(clientType, _sessionId, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
                return [];

            var taskLines = pending.Select(t => $"- [id={t.Id}] {t.Description}").ToList();
            var taskBlock = "[以下来自其他端的待办，请在本轮完成并调用 complete_cross_agent_task 标记完成]\n" + string.Join("\n", taskLines);
            return [new ChatMessage(ChatRole.System, taskBlock)];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{SessionId}] CrossAgentTaskContextProvider: pull failed.", _sessionId);
            return [];
        }
    }
}
