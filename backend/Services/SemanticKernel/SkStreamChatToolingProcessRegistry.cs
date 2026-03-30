#pragma warning disable SKEXP0080 // Semantic Kernel Process / LocalRuntime 为实验性 API
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Process;

namespace OfficeCopilot.Server.Services.SemanticKernel;

/// <summary>
/// 将主会话的「上下文准备」与/或「工具筛选 + 执行参数 + 流式用历史」包进 SK Process（LocalRuntime）；支持单步工具、单步上下文、或上下文→工具多步图。
/// </summary>
public sealed class SkStreamChatToolingProcessRegistry
{
    public const string InputEventId = "Start";

    private readonly ConcurrentDictionary<string, TurnPhases> _pending = new();
    private readonly ILogger<SkStreamChatToolingProcessRegistry> _logger;
    private static readonly object ProcessLock = new();
    private static KernelProcess? _cachedToolingOnly;
    private static KernelProcess? _cachedContextOnly;
    private static KernelProcess? _cachedFull;

    private sealed record TurnPhases(Func<Task>? Context, Func<Task>? Tooling);

    public SkStreamChatToolingProcessRegistry(ILogger<SkStreamChatToolingProcessRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>仅工具阶段走 Process（与历史行为一致）。</summary>
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

    public async Task RunToolingProcessAsync(Kernel kernel, string correlationId, CancellationToken ct) =>
        await RunProcessAsync(kernel, correlationId, StreamChatProcessKind.ToolingOnly, ct).ConfigureAwait(false);

    public async Task RunContextOnlyProcessAsync(Kernel kernel, string correlationId, CancellationToken ct) =>
        await RunProcessAsync(kernel, correlationId, StreamChatProcessKind.ContextOnly, ct).ConfigureAwait(false);

    public async Task RunFullStreamChatProcessAsync(Kernel kernel, string correlationId, CancellationToken ct) =>
        await RunProcessAsync(kernel, correlationId, StreamChatProcessKind.Full, ct).ConfigureAwait(false);

    private async Task RunProcessAsync(Kernel kernel, string correlationId, StreamChatProcessKind kind, CancellationToken ct)
    {
        var process = GetOrBuildProcess(kind);
        var ev = new KernelProcessEvent { Id = InputEventId, Data = correlationId };
        try
        {
            await LocalKernelProcessFactory.RunToEndAsync(process, kernel, ev, TimeSpan.FromMinutes(6), null)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalKernelProcessFactory.RunToEndAsync 失败，correlationId={CorrelationId} kind={Kind}", correlationId, kind);
            throw;
        }
    }

    private static KernelProcess GetOrBuildProcess(StreamChatProcessKind kind)
    {
        switch (kind)
        {
            case StreamChatProcessKind.ToolingOnly:
                return GetOrBuild(ref _cachedToolingOnly, BuildToolingOnly);
            case StreamChatProcessKind.ContextOnly:
                return GetOrBuild(ref _cachedContextOnly, BuildContextOnly);
            case StreamChatProcessKind.Full:
                return GetOrBuild(ref _cachedFull, BuildFull);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static KernelProcess GetOrBuild(ref KernelProcess? slot, Func<KernelProcess> factory)
    {
        if (slot != null)
            return slot;
        lock (ProcessLock)
        {
            slot ??= factory();
            return slot;
        }
    }

    private static KernelProcess BuildToolingOnly()
    {
        var pb = new ProcessBuilder("office-stream-chat-tooling", "OfficeCopilot 流式对话工具阶段", null!, null);
        var toolingStep = pb.AddStepFromType<SkStreamChatToolingStep>("tooling", Array.Empty<string>());
        var endStep = pb.AddEndStep();
        pb.OnInputEvent(InputEventId).SendEventTo(new ProcessFunctionTargetBuilder(toolingStep, "stream_chat_tooling_phase", "correlationId"));
        toolingStep.OnFunctionResult("stream_chat_tooling_phase").SendEventTo(new ProcessFunctionTargetBuilder(endStep, null!, null!));
        return pb.Build();
    }

    private static KernelProcess BuildContextOnly()
    {
        var pb = new ProcessBuilder("office-stream-chat-context", "OfficeCopilot 流式对话上下文阶段", null!, null);
        var contextStep = pb.AddStepFromType<SkStreamChatContextPrepStep>("contextPrep", Array.Empty<string>());
        var endStep = pb.AddEndStep();
        pb.OnInputEvent(InputEventId).SendEventTo(new ProcessFunctionTargetBuilder(contextStep, "stream_chat_context_phase", "correlationId"));
        contextStep.OnFunctionResult("stream_chat_context_phase").SendEventTo(new ProcessFunctionTargetBuilder(endStep, null!, null!));
        return pb.Build();
    }

    private static KernelProcess BuildFull()
    {
        var pb = new ProcessBuilder("office-stream-chat-full", "OfficeCopilot 流式对话上下文+工具", null!, null);
        var contextStep = pb.AddStepFromType<SkStreamChatContextPrepStep>("contextPrep", Array.Empty<string>());
        var toolingStep = pb.AddStepFromType<SkStreamChatToolingStep>("tooling", Array.Empty<string>());
        var endStep = pb.AddEndStep();
        pb.OnInputEvent(InputEventId).SendEventTo(new ProcessFunctionTargetBuilder(contextStep, "stream_chat_context_phase", "correlationId"));
        contextStep.OnFunctionResult("stream_chat_context_phase").SendEventTo(new ProcessFunctionTargetBuilder(toolingStep, "stream_chat_tooling_phase", "correlationId"));
        toolingStep.OnFunctionResult("stream_chat_tooling_phase").SendEventTo(new ProcessFunctionTargetBuilder(endStep, null!, null!));
        return pb.Build();
    }

    private enum StreamChatProcessKind
    {
        ToolingOnly,
        ContextOnly,
        Full
    }
}
#pragma warning restore SKEXP0080
