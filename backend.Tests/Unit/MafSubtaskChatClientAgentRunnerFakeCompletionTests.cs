using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services.Maf;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class MafSubtaskChatClientAgentRunnerFakeCompletionTests
{
    private sealed class FakeChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "chunk-b"));
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk-b");
        }
    }

    [Fact]
    public async Task RunStreamingAsync_invokes_onChunk_with_streamed_text()
    {
        var chatClient = new FakeChatClient();
        var services = new ServiceCollection().BuildServiceProvider();
        var runner = new MafSubtaskChatClientAgentRunner(NullLoggerFactory.Instance, NullLogger<MafSubtaskChatClientAgentRunner>.Instance);
        var sb = new StringBuilder();
        await runner.RunStreamingAsync(
            chatClient,
            services,
            tools: Array.Empty<AITool>(),
            systemPrompt: "sys",
            userContent: "hi",
            onChunkAsync: chunk =>
            {
                sb.Append(chunk);
                return Task.CompletedTask;
            },
            executionSettings: new ChatOptions(),
            CancellationToken.None);
        Assert.Contains("chunk", sb.ToString(), StringComparison.Ordinal);
    }
}
