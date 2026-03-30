#pragma warning disable SKEXP0080
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services.SemanticKernel;

/// <summary>SK Process 单步：仅执行上下文阶段闭包（与 <see cref="SkStreamChatToolingStep"/> 分离，避免同类型多实例在 LocalRuntime 下状态异常）。</summary>
public sealed class SkStreamChatContextPrepStep : KernelProcessStep
{
    [KernelFunction("stream_chat_context_phase")]
    public async Task InvokeAsync(string correlationId, Kernel kernel)
    {
        var reg = kernel.Services.GetService<SkStreamChatToolingProcessRegistry>();
        if (reg == null)
            return;
        await reg.ExecuteContextPhaseAsync(correlationId).ConfigureAwait(false);
    }
}
#pragma warning restore SKEXP0080
