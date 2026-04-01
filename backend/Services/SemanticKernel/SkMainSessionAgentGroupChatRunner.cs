#pragma warning disable SKEXP0110 // AgentGroupChat 等为评估 API
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DashScope;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace OfficeCopilot.Server.Services.SemanticKernel;

/// <summary>主会话实验路径：Host + Worker 的 <see cref="AgentGroupChat"/> 流式输出，映射为 <see cref="StreamItem"/>。</summary>
public static class SkMainSessionAgentGroupChatRunner
{
    /// <summary>
    /// 将 <paramref name="historyToUse"/> 中非 system 消息加入群聊；system 合并进 Host/Worker 的 Instructions（因 <see cref="AgentChat.AddChatMessages"/> 不接受 system）。
    /// </summary>
    public static async IAsyncEnumerable<StreamItem> InvokeStreamingAsync(
        Kernel kernel,
        ILoggerFactory loggerFactory,
        ChatHistory historyToUse,
        OpenAIPromptExecutionSettings workerExecutionSettings,
        SessionManager sessionManager,
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var systemText = "";
        var msgs = new List<ChatMessageContent>();
        foreach (var m in historyToUse)
        {
            if (m.Role == AuthorRole.System)
            {
                systemText = m.Content ?? "";
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

        var hostSettings = new OpenAIPromptExecutionSettings { MaxTokens = 384, Temperature = 0.2f };
        var hostAgent = new ChatCompletionAgent
        {
            Name = "Host",
            Instructions = hostPreamble,
            Kernel = kernel,
            LoggerFactory = loggerFactory,
            Arguments = new KernelArguments(hostSettings)
        };
        var workerAgent = new ChatCompletionAgent
        {
            Name = "Worker",
            Instructions = workerPreamble,
            Kernel = kernel,
            LoggerFactory = loggerFactory,
            Arguments = new KernelArguments(workerExecutionSettings)
        };

        var group = new AgentGroupChat(hostAgent, workerAgent)
        {
            IsComplete = false,
            ExecutionSettings = new AgentGroupChatSettings
            {
                SelectionStrategy = new SequentialSelectionStrategy(),
            }
        };
        group.ExecutionSettings.TerminationStrategy.MaximumIterations = 8;

        group.AddChatMessages(msgs);

        var toolCallArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);

        await foreach (var streaming in group.InvokeStreamingAsync(ct).ConfigureAwait(false))
        {
            if (streaming is StreamingChatMessageContent schunk)
            {
                foreach (var d in StreamingToolCallDeltaHelper.ExtractFromChunk(schunk, toolCallArgBudget))
                    yield return new StreamItem(IsWarning: false, Content: "", Kind: StreamSegmentKind.ToolCallDelta, ToolDelta: d);
            }

            if (streaming?.Content is not { Length: > 0 } text)
                continue;

            var author = (streaming.AuthorName ?? "").Trim();
            var isHost = author.Contains("Host", StringComparison.OrdinalIgnoreCase);
            if (isHost)
            {
                await NotifyTraceAsync(sessionManager, sessionId, text, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var reasoningDelta in DashScopeReasoningContext.DrainCurrentFrame())
                yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);

            var metaReasoning = OpenAiStreamingReasoningHelper.TryGetReasoningFromMetadata(streaming);
            if (!string.IsNullOrEmpty(metaReasoning))
                yield return new StreamItem(IsWarning: false, Content: metaReasoning, Kind: StreamSegmentKind.Reasoning);

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
#pragma warning restore SKEXP0110
