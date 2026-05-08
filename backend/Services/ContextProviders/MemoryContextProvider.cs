using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenWorkmate.Server.Services.Memory;

namespace OpenWorkmate.Server.Services.ContextProviders;

/// <summary>
/// MAF <see cref="MessageAIContextProvider"/>：检索会话记忆与共享记忆，注入为额外 system 消息。
/// 每轮创建新实例（捕获 turn 级参数）。
/// </summary>
internal sealed class MemoryContextProvider : MessageAIContextProvider
{
    private readonly IMemoryStoreService _memorySvc;
    private readonly string _userMessage;
    private readonly string _sessionId;
    private readonly ContextWindowConfig _ctxConfig;
    private readonly SessionManager _sessionManager;
    private readonly ILogger _logger;
    private readonly List<string> _warnings;

    public MemoryContextProvider(
        IMemoryStoreService memorySvc,
        string userMessage,
        string sessionId,
        ContextWindowConfig ctxConfig,
        SessionManager sessionManager,
        ILogger logger,
        List<string> warnings)
    {
        _memorySvc = memorySvc;
        _userMessage = userMessage;
        _sessionId = sessionId;
        _ctxConfig = ctxConfig;
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
            await ContextProviderNotifier.StatusAsync(_sessionManager, _sessionId, "正在检索相关记忆…", cancellationToken).ConfigureAwait(false);

            var sessionTopK = Math.Clamp(_ctxConfig.MemorySessionTopK, 1, 20);
            var sharedTopK = Math.Clamp(_ctxConfig.MemorySharedTopK, 1, 20);
            var sessionResults = await _memorySvc.SearchAsync(_userMessage, sessionTopK, _sessionId, cancellationToken).ConfigureAwait(false);
            var sharedResults = await _memorySvc.SearchSharedAsync(_userMessage, sharedTopK, cancellationToken).ConfigureAwait(false);

            var memTrace = AgentTraceFormatter.BuildMemoryTrace(sessionResults, sharedResults, sessionTopK, sharedTopK);
            await ContextProviderNotifier.TraceAsync(_sessionManager, _sessionId, "memory", memTrace.Title, memTrace.Detail, cancellationToken).ConfigureAwait(false);

            if (sessionResults.Count == 0 && sharedResults.Count == 0)
                return [];

            var parts = new List<string>();
            if (sessionResults.Count > 0)
                parts.Add("[以下是与当前对话相关的长期记忆，供参考]\n[本会话记忆]\n" + string.Join("\n", sessionResults.Select(r => "- " + r.Text)));
            if (sharedResults.Count > 0)
                parts.Add("[来自共享记忆]\n" + string.Join("\n", sharedResults.Select(r => "- " + r.Text)));
            var memoryBlock = string.Join("\n\n", parts);

            if (_ctxConfig.MemoryInjectionMaxChars > 0 && memoryBlock.Length > _ctxConfig.MemoryInjectionMaxChars)
                memoryBlock = memoryBlock.AsSpan(0, _ctxConfig.MemoryInjectionMaxChars).ToString() + "\n（前文已截断）";

            return [new ChatMessage(ChatRole.System, memoryBlock)];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{SessionId}] MemoryContextProvider: search failed.", _sessionId);
            var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
            _warnings.Add("记忆检索失败：" + friendly + " 当前对话未注入长期记忆。");
            await ContextProviderNotifier.TraceAsync(_sessionManager, _sessionId, "memory", "长期记忆检索失败", friendly, cancellationToken).ConfigureAwait(false);
            return [];
        }
    }
}
