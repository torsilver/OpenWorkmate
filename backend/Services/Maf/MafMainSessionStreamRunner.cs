using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Diagnostics;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DashScope;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>使用 MAF <see cref="ChatClientAgent"/> + MEAI 消息流映射主会话 <see cref="StreamItem"/>（主路径；工具来自 <see cref="MafRuntimeToolFacade"/>）。</summary>
public static class MafMainSessionStreamRunner
{
    public static async IAsyncEnumerable<StreamItem> EnumerateStreamingAsync(
        IChatClient chatClient,
        IChatRuntimeAccessor runtime,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        List<ChatMessage> history,
        ChatOptions settings,
        SessionManager sessionManager,
        string sessionId,
        SessionState state,
        ContextWindowConfig ctxConfig,
        StreamPassOutcome outcome,
        int contextAttemptIndex,
        bool requireToolInvocation = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var clientType = sessionManager.GetClientType(sessionId);
        using var activity = MafActivitySource.Activity.StartActivity("Maf.MainSession.Stream", ActivityKind.Internal);
        activity?.SetTag("sessionId", sessionId);
        activity?.SetTag("clientType", clientType ?? "");
        activity?.SetTag("requireToolInvocation", requireToolInvocation);

        var tools = new List<AITool>(MafRuntimeToolFacade.GetToolsForSession(runtime, clientType, sessionId));

        var chatOpts = MafChatOptionsMapper.ToChatOptions(settings, tools, requireToolInvocation);
        var agentOpts = new ChatClientAgentOptions
        {
            ChatOptions = chatOpts,
        };

        var agent = new ChatClientAgent(chatClient, agentOpts, loggerFactory, services);
        var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var messages = history;
        var runOpts = new ChatClientAgentRunOptions(chatOpts);

        var toolBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        var callState = new Dictionary<string, (string Name, string ArgsSoFar)>(StringComparer.Ordinal);

        var allowContextRetry = contextAttemptIndex == 0 && !ctxConfig.PassThroughContext && ctxConfig.ContextLengthRetryEnabled;

        await using var enumerator = agent.RunStreamingAsync(messages, session, runOpts, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (allowContextRetry && ContextLengthRetryHelper.IsContextLengthError(ex))
            {
                ContextLengthRetryHelper.TrimHistoryForRetry(state.History, ctxConfig.ContextLengthRetryMaxTurns, ctxConfig);
                outcome.ContextLengthRetryRequested = true;
                yield break;
            }

            if (!moved)
                break;

            var update = enumerator.Current;

            foreach (var reasoningDelta in DashScopeReasoningSessionBridge.DrainForSession(sessionId))
                yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

            foreach (var reasoningDelta in DashScopeReasoningContext.DrainCurrentFrame())
                yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

            foreach (var d in MafToolCallDeltaExtractor.ExtractFromAgentResponseUpdate(update, toolBudget, callState))
                yield return new StreamItem(IsWarning: false, Content: "", Kind: StreamSegmentKind.ToolCallDelta, ToolDelta: d);

            var text = update.Text;
            if (text is { Length: > 0 })
                yield return new StreamItem(IsWarning: false, Content: text);
        }
    }
}
