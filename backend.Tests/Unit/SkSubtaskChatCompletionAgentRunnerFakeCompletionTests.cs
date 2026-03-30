using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OfficeCopilot.Server.Services.SemanticKernel;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class SkSubtaskChatCompletionAgentRunnerFakeCompletionTests
{
    private sealed class FakeStreamingCompletion : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessageContent>>(new List<ChatMessageContent>());

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "x") { Content = "chunk-b" };
        }
    }

    [Fact]
    public async Task RunStreamingAsync_invokes_onChunk_with_streamed_text()
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService, FakeStreamingCompletion>();
        var kernel = builder.Build();
        var runner = new SkSubtaskChatCompletionAgentRunner(NullLoggerFactory.Instance, NullLogger<SkSubtaskChatCompletionAgentRunner>.Instance);
        var sb = new StringBuilder();
        await runner.RunStreamingAsync(
            kernel,
            chatServiceId: null,
            systemPrompt: "sys",
            userContent: "hi",
            allowedFunctions: Array.Empty<KernelFunction>(),
            onChunkAsync: chunk =>
            {
                sb.Append(chunk);
                return Task.CompletedTask;
            },
            executionSettings: new OpenAIPromptExecutionSettings(),
            CancellationToken.None);
        Assert.Contains("chunk", sb.ToString(), StringComparison.Ordinal);
    }
}
