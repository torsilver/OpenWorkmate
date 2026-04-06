namespace OfficeCopilot.Server.Services.Chat;

/// <summary>主会话单轮的阶段编排：上下文拆为 Part1（至知识库结束，产生 warnings）与 Part2（跨端待办与计划注入），再执行工具阶段。</summary>
public interface IChatTurnProcessCoordinator
{
    Task RunContextPreparationPart1Async(StreamChatTurnContext context, CancellationToken cancellationToken = default);
    Task RunContextPreparationPart2Async(StreamChatTurnContext context, CancellationToken cancellationToken = default);
    Task RunToolingPhaseAsync(StreamChatTurnContext context, CancellationToken cancellationToken = default);
}
