using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Diagnostics;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 主会话「上下文准备」与「工具筛选 + 执行参数 + 流式用历史」的按回合注册表。
/// 原 SK Process（LocalRuntime）仅用于串行调用这些闭包；现已改为<strong>直接顺序 await</strong>，等价且去掉 alpha 依赖。
/// 可选：后续用 <c>Microsoft.Agents.AI.Workflows</c> 编排长流程（checkpoint/HITL）；当前用 <see cref="Activity"/> 标记阶段便于可观测性。
/// </summary>
public sealed class ChatToolingRegistry
{
    public const string InputEventId = "Start";

    private readonly ConcurrentDictionary<string, TurnPhases> _pending = new();
    private readonly ILogger<ChatToolingRegistry> _logger;

    private sealed record TurnPhases(Func<Task>? Context, Func<Task>? Tooling);

    public ChatToolingRegistry(ILogger<ChatToolingRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>仅工具阶段（与历史行为一致）。</summary>
    public void Register(string correlationId, Func<Task> toolingPhase) =>
        Register(correlationId, null, toolingPhase);

    public void Register(string correlationId, Func<Task>? contextPhase, Func<Task>? toolingPhase)
    {
        if (string.IsNullOrEmpty(correlationId))
            throw new ArgumentException("correlationId required", nameof(correlationId));
        _pending[correlationId] = new TurnPhases(contextPhase, toolingPhase);
    }

    public void Unregister(string correlationId) => _pending.TryRemove(correlationId, out _);

    public Task ExecuteContextPhaseAsync(string correlationId) =>
        _pending.TryGetValue(correlationId, out var p) && p.Context != null
            ? p.Context()
            : Task.CompletedTask;

    public Task ExecuteToolingPhaseAsync(string correlationId) =>
        _pending.TryGetValue(correlationId, out var p) && p.Tooling != null
            ? p.Tooling()
            : Task.CompletedTask;

    /// <summary>仅工具阶段：直接执行已注册闭包（不再经 SK KernelProcess）。</summary>
    public Task RunToolingProcessAsync(string correlationId, CancellationToken ct) =>
        ExecuteToolingPhaseAsync(correlationId);

    /// <summary>仅上下文阶段。</summary>
    public Task RunContextOnlyProcessAsync(string correlationId, CancellationToken ct) =>
        ExecuteContextPhaseAsync(correlationId);

    /// <summary>上下文 Part1+2 后执行工具阶段。</summary>
    public async Task RunFullStreamChatProcessAsync(string correlationId, CancellationToken ct)
    {
        using var full = MafActivitySource.Activity.StartActivity("ChatTooling.FullRun", ActivityKind.Internal);
        full?.SetTag("correlationId", correlationId);
        try
        {
            using (MafActivitySource.Activity.StartActivity("ChatTooling.ContextPhase", ActivityKind.Internal))
                await ExecuteContextPhaseAsync(correlationId).ConfigureAwait(false);
            using (MafActivitySource.Activity.StartActivity("ChatTooling.ToolingPhase", ActivityKind.Internal))
                await ExecuteToolingPhaseAsync(correlationId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream chat 编排阶段失败，correlationId={CorrelationId}", correlationId);
            throw;
        }
    }
}
