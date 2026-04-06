using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using OfficeCopilot.Server.Diagnostics;

namespace OfficeCopilot.Server.Services.Chat.Executors;

/// <summary>
/// 构建主会话单轮的三阶段 MAF Workflow：ContextPrepPart1 → ContextPrepPart2 → ToolingPhase。
/// 使用 <see cref="FunctionExecutor{TInput, TOutput}"/> 按阶段委托给 <see cref="ChatService"/> 内部方法。
/// 每个 superstep 之间自动创建 <see cref="CheckpointManager"/> 检查点，支持上下文长度重试等恢复场景。
/// </summary>
internal static class ChatTurnWorkflow
{
    private static CheckpointManager? _checkpointManager;

    public static Workflow Build(IServiceProvider sp)
    {
        var part1 = new FunctionExecutor<StreamChatTurnContext, StreamChatTurnContext>(
            "ContextPrepPart1",
            async (turn, ctx, ct) =>
            {
                var chat = sp.GetRequiredService<ChatService>();
                await chat.RunStreamChatContextPhasePart1Async(turn, ct).ConfigureAwait(false);
                return turn;
            });

        var part2 = new FunctionExecutor<StreamChatTurnContext, StreamChatTurnContext>(
            "ContextPrepPart2",
            async (turn, ctx, ct) =>
            {
                var chat = sp.GetRequiredService<ChatService>();
                await chat.RunStreamChatContextPhasePart2Async(turn, ct).ConfigureAwait(false);
                return turn;
            });

        var tooling = new FunctionExecutor<StreamChatTurnContext, StreamChatTurnContext>(
            "ToolingPhase",
            async (turn, ctx, ct) =>
            {
                var chat = sp.GetRequiredService<ChatService>();
                await chat.RunStreamChatToolingPhaseAsync(turn, ct).ConfigureAwait(false);
                return turn;
            });

        var builder = new WorkflowBuilder(part1)
            .WithOpenTelemetry(activitySource: MafActivitySource.Activity);
        builder.AddEdge(part1, part2);
        builder.AddEdge(part2, tooling);
        return builder.Build();
    }

    public static async Task RunAsync(Workflow workflow, StreamChatTurnContext turn, CancellationToken ct)
    {
        _checkpointManager ??= CheckpointManager.CreateInMemory();
        await InProcessExecution.RunAsync(workflow, turn, _checkpointManager, cancellationToken: ct).ConfigureAwait(false);
    }
}
