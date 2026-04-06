using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services.Memory;

namespace OfficeCopilot.Server.Services.ContextProviders;

/// <summary>
/// MAF <see cref="MessageAIContextProvider"/>：检索知识库，注入为额外 system 消息。
/// 每轮创建新实例（捕获 turn 级参数）。
/// </summary>
internal sealed class KnowledgeBaseContextProvider : MessageAIContextProvider
{
    private readonly IMemoryStoreService _memorySvc;
    private readonly string _knowledgeBaseId;
    private readonly string _userMessage;
    private readonly string _sessionId;
    private readonly SessionManager _sessionManager;
    private readonly ILogger _logger;
    private readonly List<string> _warnings;

    public KnowledgeBaseContextProvider(
        IMemoryStoreService memorySvc,
        string knowledgeBaseId,
        string userMessage,
        string sessionId,
        SessionManager sessionManager,
        ILogger logger,
        List<string> warnings)
    {
        _memorySvc = memorySvc;
        _knowledgeBaseId = knowledgeBaseId;
        _userMessage = userMessage;
        _sessionId = sessionId;
        _sessionManager = sessionManager;
        _logger = logger;
        _warnings = warnings;
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (!_memorySvc.IsAvailable)
            return [];

        try
        {
            await ContextProviderNotifier.StatusAsync(_sessionManager, _sessionId, "正在检索知识库…", cancellationToken).ConfigureAwait(false);

            var kbResults = await _memorySvc.SearchKnowledgeBaseAsync(_knowledgeBaseId.Trim(), _userMessage, 5, cancellationToken).ConfigureAwait(false);
            var kbTrace = AgentTraceFormatter.BuildKnowledgeBaseTrace(_knowledgeBaseId.Trim(), kbResults);
            await ContextProviderNotifier.TraceAsync(_sessionManager, _sessionId, "knowledgeBase", kbTrace.Title, kbTrace.Detail, cancellationToken).ConfigureAwait(false);

            if (kbResults.Count == 0)
                return [];

            var kbLines = kbResults.Select(r => $"- {r.Text}").ToList();
            var kbBlock = "[以下来自知识库的参考内容]\n" + string.Join("\n", kbLines);

            return [new ChatMessage(ChatRole.System, kbBlock)];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{SessionId}] KnowledgeBaseContextProvider: search failed for {KbId}.", _sessionId, _knowledgeBaseId);
            var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
            _warnings.Add("知识库检索失败：" + friendly);
            await ContextProviderNotifier.TraceAsync(_sessionManager, _sessionId, "knowledgeBase", "知识库检索失败", friendly, cancellationToken).ConfigureAwait(false);
            return [];
        }
    }
}
