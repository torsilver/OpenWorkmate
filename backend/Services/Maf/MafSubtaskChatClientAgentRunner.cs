using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Maf;

/// <summary>使用 MAF <see cref="ChatClientAgent"/> 流式执行子任务。</summary>
public sealed class MafSubtaskChatClientAgentRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MafSubtaskChatClientAgentRunner> _logger;

    public MafSubtaskChatClientAgentRunner(ILoggerFactory loggerFactory, ILogger<MafSubtaskChatClientAgentRunner> logger)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task RunStreamingAsync(
        IChatClient chatClient,
        IServiceProvider services,
        IReadOnlyList<AITool> tools,
        string systemPrompt,
        string userContent,
        Func<string, Task> onChunkAsync,
        ChatOptions executionSettings,
        CancellationToken ct,
        Func<ToolCallStreamDelta, Task>? onToolCallDeltaAsync = null)
    {
        var chatOpts = MafChatOptionsMapper.ToChatOptions(executionSettings, tools.ToList());
        chatOpts.Instructions = systemPrompt;
        var agentOpts = new ChatClientAgentOptions
        {
            ChatOptions = chatOpts,
        };
        var agent = new ChatClientAgent(chatClient, agentOpts, _loggerFactory, services);
        var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var messages = new List<ChatMessage> { new(ChatRole.User, userContent) };
        var runOpts = new ChatClientAgentRunOptions(chatOpts);
        var toolCallArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        var callState = new Dictionary<string, (string Name, string ArgsSoFar)>(StringComparer.Ordinal);

        try
        {
            await foreach (var update in agent.RunStreamingAsync(messages, session, runOpts, ct).ConfigureAwait(false))
            {
                if (onToolCallDeltaAsync != null)
                {
                    foreach (var d in MafToolCallDeltaExtractor.ExtractFromAgentResponseUpdate(update, toolCallArgBudget, callState))
                        await onToolCallDeltaAsync(d).ConfigureAwait(false);
                }

                var text = update.Text;
                if (text is { Length: > 0 })
                    await onChunkAsync(text).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatClientAgent.RunStreamingAsync 子任务失败");
            throw;
        }
    }
}
