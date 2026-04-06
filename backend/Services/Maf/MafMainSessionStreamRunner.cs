using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Diagnostics;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DashScope;
using OfficeCopilot.Server.Services.ToolInvocation;

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
        MessageAIContextProvider[]? contextProviders = null,
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
        var messages = history;
        var runOpts = new ChatClientAgentRunOptions(chatOpts);

        var toolBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        var callState = new Dictionary<string, (string Name, string ArgsSoFar)>(StringComparer.Ordinal);

        await foreach (var update in agent.RunStreamingAsync(messages, session, runOpts, ct).ConfigureAwait(false))
        {

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
