using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services.DashScope;
using OfficeCopilot.Server.Services.OpenAiCompat;
using OfficeCopilot.Server.Services.ToolInvocation;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>
/// 主会话 Host + Worker 多 Agent 编排（MAF <see cref="AgentWorkflowBuilder.BuildSequential"/>）。
/// Host 输出走 <c>agent_trace</c>，Worker 输出流式 yield 给用户。
/// </summary>
public static class MafAgentGroupChatSessionRunner
{
    public static async IAsyncEnumerable<StreamItem> InvokeStreamingAsync(
        IChatRuntimeAccessor runtime,
        ILoggerFactory loggerFactory,
        List<ChatMessage> historyToUse,
        ChatOptions workerBaseChatOptions,
        SessionManager sessionManager,
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var chatClient = runtime.GetChatClient();
        if (chatClient == null)
            yield break;
        var pluginServices = runtime.GetPluginServices();
        if (pluginServices == null)
            yield break;

        var systemText = "";
        var msgs = new List<ChatMessage>();
        foreach (var m in historyToUse)
        {
            if (m.Role == ChatRole.System)
            {
                systemText = m.Text ?? "";
                continue;
            }
            msgs.Add(m);
        }
        if (msgs.Count == 0)
            yield break;

        var hostPreamble = string.IsNullOrWhiteSpace(systemText)
            ? "你是主会话 Host：先用极简要点确认用户意图与约束，再让 Worker 完成具体解答与工具调用。"
            : systemText + "\n\n你是主会话 Host：先用极简要点确认用户意图与约束，再让 Worker 完成具体解答与工具调用。";
        var workerPreamble = string.IsNullOrWhiteSpace(systemText)
            ? "你是执行助手 Worker：根据对话与可用工具完成用户任务，向用户输出最终可用答案。"
            : systemText + "\n\n你是执行助手 Worker：根据对话与可用工具完成用户任务，向用户输出最终可用答案。";

        // --- Build agents ---
        var hostOpts = new ChatClientAgentOptions
        {
            Name = "Host",
            ChatOptions = new ChatOptions { MaxOutputTokens = 384, Temperature = 0.2f, Instructions = hostPreamble },
        };
        var hostAgent = new ChatClientAgent(chatClient, hostOpts, loggerFactory, pluginServices);

        var clientType = sessionManager.GetClientType(sessionId);
        var wpsHostKind = string.Equals(clientType, "wps", StringComparison.OrdinalIgnoreCase)
            ? sessionManager.GetWpsHostKind(sessionId)
            : null;
        var tools = new List<AITool>(MafRuntimeToolFacade.GetToolsForSession(runtime, clientType, sessionId, wpsHostKind));
        var workerChatOpts = MafChatOptionsMapper.ToChatOptions(workerBaseChatOptions, tools);
        workerChatOpts.Instructions = workerPreamble;
        var workerAgentOpts = new ChatClientAgentOptions
        {
            Name = "Worker",
            ChatOptions = workerChatOpts,
        };
        var pipelineServices = pluginServices.GetRequiredService<ToolInvocationPipelineServices>();
        var toolRegistry = runtime.ToolRegistry;
        var workerAgent = new ChatClientAgent(chatClient, workerAgentOpts, loggerFactory, pluginServices)
            .AsBuilder()
            .Use(ToolInvocationMiddleware.Create(toolRegistry, pipelineServices))
            .Build();

        // --- Build sequential workflow ---
        var workflow = AgentWorkflowBuilder.BuildSequential([hostAgent, workerAgent]);

        var toolCallArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        var callState = new Dictionary<string, (string Name, string ArgsSoFar)>(StringComparer.Ordinal);
        var hostSb = new StringBuilder();
        var metaState = new MafStreamDeltaMetadataState();

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, msgs, cancellationToken: ct).ConfigureAwait(false);
        await foreach (var evt in run.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            if (evt is AgentResponseUpdateEvent updateEvt)
            {
                var isHost = updateEvt.ExecutorId?.Contains("Host", StringComparison.OrdinalIgnoreCase) == true;

                if (isHost)
                {
                    if (updateEvt.Update?.Text is { Length: > 0 } ht)
                        hostSb.Append(ht);
                }
                else
                {
                    var streaming = updateEvt.Update;
                    if (streaming == null) continue;

                    foreach (var d in MafToolCallDeltaExtractor.ExtractFromAgentResponseUpdate(streaming, toolCallArgBudget, callState))
                        yield return new StreamItem(IsWarning: false, Content: "", Kind: StreamSegmentKind.ToolCallDelta, ToolDelta: d);

                    foreach (var reasoningDelta in DashScopeReasoningSessionBridge.DrainForSession(sessionId))
                        yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

                    foreach (var reasoningDelta in DashScopeReasoningContext.DrainCurrentFrame())
                        yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

                    foreach (var usageJson in OpenAiStreamUsageSessionBridge.DrainForSession(sessionId))
                    {
                        if (!string.IsNullOrEmpty(usageJson))
                            yield return new StreamItem(IsWarning: false, Content: usageJson, Kind: StreamSegmentKind.StreamUsage);
                    }

                    foreach (var metaItem in MafChatResponseStreamMetadataExtractor.ExtractFromAgentUpdate(streaming, metaState))
                        yield return metaItem;

                    if (streaming.Text is { Length: > 0 } text)
                        yield return new StreamItem(IsWarning: false, Content: text);
                }
            }
        }

        var hostCombined = hostSb.ToString().Trim();
        if (hostCombined.Length > 0)
            await NotifyTraceAsync(sessionManager, sessionId, hostCombined, ct).ConfigureAwait(false);
    }

    private static async Task NotifyTraceAsync(SessionManager sessionManager, string sessionId, string detail, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(sessionId)) return;
        var msg = new WsMessage
        {
            Type = "agent_trace",
            Content = "Host",
            TraceCategory = "agent_phase",
            TraceTitle = "Host",
            TraceDetail = AgentTraceFormatter.TruncateDetail(detail)
        };
        var json = System.Text.Json.JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await sessionManager.SendToAsync(sessionId, json).ConfigureAwait(false);
    }
}
