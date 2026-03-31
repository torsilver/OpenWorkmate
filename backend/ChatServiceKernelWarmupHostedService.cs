namespace OfficeCopilot.Server;

/// <summary>
/// 在宿主启动时异步预热 <see cref="ChatService"/> 的 Kernel，避免在单例构造函数中同步阻塞。
/// </summary>
public sealed class ChatServiceKernelWarmupHostedService : IHostedService
{
    private readonly ChatService _chat;

    public ChatServiceKernelWarmupHostedService(ChatService chat)
    {
        _chat = chat;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _chat.RebuildKernelAsync(skipUserToolIndexSync: true).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
