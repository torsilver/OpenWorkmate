using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Diagnostics;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DashScope;
using OfficeCopilot.Server.Services.DynamicTooling;
using OfficeCopilot.Server.Services.ToolInvocation;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>使用 MAF <see cref="ChatClientAgent"/> + MEAI 消息流映射主会话 <see cref="StreamItem"/>（主路径；工具来自 <see cref="MafRuntimeToolFacade"/> 或动态扩容）。</summary>
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
        MessageAIContextProvider[]? contextProviders = null,
        IReadOnlyList<AITool>? toolsForAgent = null,
        DynamicToolingTurnState? dynamicTooling = null,
        bool mergePlanIntoDynamicBootstrap = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var clientType = sessionManager.GetClientType(sessionId);
        var wpsHostKindForSession = string.Equals(clientType, "wps", StringComparison.OrdinalIgnoreCase)
            ? sessionManager.GetWpsHostKind(sessionId)
            : null;
        using var activity = MafActivitySource.Activity.StartActivity("Maf.MainSession.Stream", ActivityKind.Internal);
        activity?.SetTag("sessionId", sessionId);
        activity?.SetTag("clientType", clientType ?? "");
        activity?.SetTag("requireToolInvocation", requireToolInvocation);
        activity?.SetTag("dynamicTooling", dynamicTooling != null && dynamicTooling.Config.Enabled);

        if (dynamicTooling is { Config.Enabled: true } dts)
        {
            using (DynamicToolingTurnScope.Push(dts))
            {
                for (var outer = 0; outer < dts.Config.MaxOuterLoops; outer++)
                {
                    dts.ClearExpansionFlag();
                    var active = SessionToolResolver.BuildDynamicActiveToolList(
                        runtime.ToolRegistry, dts, clientType, sessionId, mergePlanIntoDynamicBootstrap);
                    await foreach (var item in RunSinglePassStreamingAsync(
                                       chatClient, runtime, loggerFactory, services, history, settings,
                                       sessionId, state, ctxConfig, outcome, contextAttemptIndex,
                                       requireToolInvocation, contextProviders, active, ct).ConfigureAwait(false))
                        yield return item;

                    if (!dts.ExpansionOccurredInLastPass)
                        break;
                }
            }

            yield break;
        }

        var tools = toolsForAgent != null
            ? new List<AITool>(toolsForAgent)
            : new List<AITool>(MafRuntimeToolFacade.GetToolsForSession(runtime, clientType, sessionId, wpsHostKindForSession));
        await foreach (var item in RunSinglePassStreamingAsync(
                           chatClient, runtime, loggerFactory, services, history, settings,
                           sessionId, state, ctxConfig, outcome, contextAttemptIndex,
                           requireToolInvocation, contextProviders, tools, ct).ConfigureAwait(false))
            yield return item;
    }

    private static async IAsyncEnumerable<StreamItem> RunSinglePassStreamingAsync(
        IChatClient chatClient,
        IChatRuntimeAccessor runtime,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        List<ChatMessage> history,
        ChatOptions settings,
        string sessionId,
        SessionState state,
        ContextWindowConfig ctxConfig,
        StreamPassOutcome outcome,
        int contextAttemptIndex,
        bool requireToolInvocation,
        MessageAIContextProvider[]? contextProviders,
        IReadOnlyList<AITool> tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var toolList = tools is List<AITool> tl ? tl : new List<AITool>(tools);
        var chatOpts = MafChatOptionsMapper.ToChatOptions(settings, toolList, requireToolInvocation);
        var agentOpts = new ChatClientAgentOptions { ChatOptions = chatOpts };
        var pipelineServices = services.GetRequiredService<ToolInvocationPipelineServices>();
        var toolRegistry = runtime.ToolRegistry;
        var allowContextRetry = contextAttemptIndex == 0 && !ctxConfig.PassThroughContext && ctxConfig.ContextLengthRetryEnabled;
        var builder = new ChatClientAgent(chatClient, agentOpts, loggerFactory, services)
            .AsBuilder()
            .Use(ToolInvocationMiddleware.Create(toolRegistry, pipelineServices))
            .Use(AgentRunMiddleware.CreateContextLengthRetry(state, ctxConfig, outcome, allowContextRetry));
        if (contextProviders is { Length: > 0 })
            builder = builder.UseAIContextProviders(contextProviders);
        var agent = builder.Build();
        var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var runOpts = new ChatClientAgentRunOptions(chatOpts);

        var toolBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        var callState = new Dictionary<string, (string Name, string ArgsSoFar)>(StringComparer.Ordinal);

        var dtsBind = DynamicToolingTurnScope.Current;
        if (dtsBind != null)
        {
            dtsBind.ToolListMutationTarget = toolList;
            pipelineServices.Logger.LogDebug(
                "[DynamicTools] ToolListMutationTarget bound passSession={PassSession} dtsSession={DtsSession} initialToolCount={Count}",
                sessionId,
                dtsBind.SessionIdForTools ?? "?",
                toolList.Count);
        }

        try
        {
            await foreach (var update in agent.RunStreamingAsync(history, session, runOpts, ct).ConfigureAwait(false))
            {
                // 百炼 Tap 在一次 Read 内会处理缓冲区里多条完整 SSE 行并把 reasoning 先入队；若本 update 来自较早的 data 行，
                // 先 Drain 会把「较晚行」的 reasoning 挪到当前 tool 之前，颠倒与上游 data: 行顺序。故先出 tool，再 Drain。
                foreach (var d in MafToolCallDeltaExtractor.ExtractFromAgentResponseUpdate(update, toolBudget, callState))
                    yield return new StreamItem(IsWarning: false, Content: "", Kind: StreamSegmentKind.ToolCallDelta, ToolDelta: d);

                foreach (var reasoningDelta in DashScopeReasoningSessionBridge.DrainForSession(sessionId))
                    yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

                foreach (var reasoningDelta in DashScopeReasoningContext.DrainCurrentFrame())
                    yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

                var text = update.Text;
                if (text is { Length: > 0 })
                    yield return new StreamItem(IsWarning: false, Content: text);
            }
        }
        finally
        {
            if (DynamicToolingTurnScope.Current is { } dtsUnbind)
            {
                pipelineServices.Logger.LogDebug(
                    "[DynamicTools] ToolListMutationTarget unbound passSession={PassSession} dtsSession={DtsSession}",
                    sessionId,
                    dtsUnbind.SessionIdForTools ?? "?");
                dtsUnbind.ToolListMutationTarget = null;
            }
        }
    }
}
