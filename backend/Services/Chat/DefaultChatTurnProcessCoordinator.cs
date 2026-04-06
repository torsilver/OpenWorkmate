using Microsoft.Extensions.DependencyInjection;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Chat;

/// <summary>默认协调器：委托给 <see cref="ChatService"/> 内部阶段方法；通过 <see cref="IServiceProvider"/> 解析以避免构造期循环依赖。</summary>
public sealed class DefaultChatTurnProcessCoordinator(IServiceProvider serviceProvider) : IChatTurnProcessCoordinator
{
    public Task RunContextPreparationPart1Async(StreamChatTurnContext context, CancellationToken cancellationToken = default) =>
        serviceProvider.GetRequiredService<ChatService>().RunStreamChatContextPhasePart1Async(context, cancellationToken);

    public Task RunContextPreparationPart2Async(StreamChatTurnContext context, CancellationToken cancellationToken = default) =>
        serviceProvider.GetRequiredService<ChatService>().RunStreamChatContextPhasePart2Async(context, cancellationToken);

    public Task RunToolingPhaseAsync(StreamChatTurnContext context, CancellationToken cancellationToken = default) =>
        serviceProvider.GetRequiredService<ChatService>().RunStreamChatToolingPhaseAsync(context, cancellationToken);
}
