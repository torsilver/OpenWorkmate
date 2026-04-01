using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.SemanticKernel;

/// <summary>使用 SK <see cref="ChatCompletionAgent"/> 流式执行子任务（与直连 IChatCompletionService 行为对齐，便于实验 Agent 路径）。</summary>
public sealed class SkSubtaskChatCompletionAgentRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SkSubtaskChatCompletionAgentRunner> _logger;

    public SkSubtaskChatCompletionAgentRunner(ILoggerFactory loggerFactory, ILogger<SkSubtaskChatCompletionAgentRunner> logger)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task RunStreamingAsync(
        Kernel kernel,
        string? chatServiceId,
        string systemPrompt,
        string userContent,
        IReadOnlyList<KernelFunction> allowedFunctions,
        Func<string, Task> onChunkAsync,
        OpenAIPromptExecutionSettings executionSettings,
        CancellationToken ct,
        Func<ToolCallStreamDelta, Task>? onToolCallDeltaAsync = null)
    {
        var history = new ChatHistory();
        history.AddUserMessage(userContent);

        var settings = executionSettings;
        if (!string.IsNullOrWhiteSpace(chatServiceId))
            settings.ServiceId = chatServiceId;

        var args = new KernelArguments(settings);
        var agent = new ChatCompletionAgent
        {
            Name = "SubtaskRunner",
            Instructions = systemPrompt,
            Kernel = kernel,
            LoggerFactory = _loggerFactory,
            Arguments = args
        };

        var invokeOptions = new AgentInvokeOptions { Kernel = kernel, KernelArguments = args };
        var toolCallArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await foreach (var item in agent.InvokeStreamingAsync(history, thread: null, invokeOptions, ct).ConfigureAwait(false))
            {
                var streaming = item.Message;
                if (onToolCallDeltaAsync != null && streaming is StreamingChatMessageContent schunk)
                {
                    foreach (var d in StreamingToolCallDeltaHelper.ExtractFromChunk(schunk, toolCallArgBudget))
                        await onToolCallDeltaAsync(d).ConfigureAwait(false);
                }

                if (streaming?.Content is { Length: > 0 } text)
                    await onChunkAsync(text).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatCompletionAgent.InvokeStreamingAsync 子任务失败");
            throw;
        }
    }
}
