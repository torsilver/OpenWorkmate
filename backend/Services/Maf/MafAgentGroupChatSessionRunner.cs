using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DashScope;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>主会话实验路径：Host + Worker 各一轮（MAF ChatClientAgent），流式输出映射为 <see cref="StreamItem"/>。</summary>
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

        var hostMessages = new List<ChatMessage>(msgs);

        var hostOpts = new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                MaxOutputTokens = 384,
                Temperature = 0.2f,
                Instructions = hostPreamble,
            },
        };
        var hostAgent = new ChatClientAgent(chatClient, hostOpts, loggerFactory, pluginServices);
        var hostSession = await hostAgent.CreateSessionAsync(ct).ConfigureAwait(false);
        var hostRun = new ChatClientAgentRunOptions(hostOpts.ChatOptions ?? new ChatOptions());
        var hostSb = new StringBuilder();
        await foreach (var u in hostAgent.RunStreamingAsync(hostMessages, hostSession, hostRun, ct).ConfigureAwait(false))
        {
            if (u.Text is { Length: > 0 } ht)
                hostSb.Append(ht);
        }
        var hostCombined = hostSb.ToString().Trim();
        if (hostCombined.Length > 0)
            await NotifyTraceAsync(sessionManager, sessionId, hostCombined, ct).ConfigureAwait(false);

        var workerMessages = new List<ChatMessage>(msgs)
        {
            new(ChatRole.Assistant, hostCombined)
        };

        var clientType = sessionManager.GetClientType(sessionId);
        var tools = new List<AITool>(MafRuntimeToolFacade.GetToolsForSession(runtime, clientType, sessionId));

        var workerChatOpts = MafChatOptionsMapper.ToChatOptions(workerBaseChatOptions, tools);
        workerChatOpts.Instructions = workerPreamble;
        var workerAgentOpts = new ChatClientAgentOptions
        {
            ChatOptions = workerChatOpts,
        };
        var workerAgent = new ChatClientAgent(chatClient, workerAgentOpts, loggerFactory, pluginServices);
        var workerSession = await workerAgent.CreateSessionAsync(ct).ConfigureAwait(false);
        var workerRun = new ChatClientAgentRunOptions(workerChatOpts);
        var toolCallArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        var callState = new Dictionary<string, (string Name, string ArgsSoFar)>(StringComparer.Ordinal);

        await foreach (var streaming in workerAgent.RunStreamingAsync(workerMessages, workerSession, workerRun, ct).ConfigureAwait(false))
        {
            foreach (var d in MafToolCallDeltaExtractor.ExtractFromAgentResponseUpdate(streaming, toolCallArgBudget, callState))
                yield return new StreamItem(IsWarning: false, Content: "", Kind: StreamSegmentKind.ToolCallDelta, ToolDelta: d);

            foreach (var reasoningDelta in DashScopeReasoningContext.DrainCurrentFrame())
                yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

            if (streaming.Text is { Length: > 0 } text)
                yield return new StreamItem(IsWarning: false, Content: text);
        }
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
