namespace OfficeCopilot.Server;

/// <summary>
/// 在宿主启动时异步预热 <see cref="ChatService"/> 的运行时，避免在单例构造函数中同步阻塞。
/// </summary>
public sealed class ChatServiceWarmupHostedService : IHostedService
{
    private readonly ChatService _chat;

    public ChatServiceWarmupHostedService(ChatService chat)
    {
        _chat = chat;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _chat.RebuildRuntimeAsync().ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
