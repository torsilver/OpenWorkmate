#pragma warning disable SKEXP0080
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services.SemanticKernel;

/// <summary>SK Process 单步：执行 ChatService 注册的工具阶段闭包。</summary>
public sealed class SkStreamChatToolingStep : KernelProcessStep
{
    [KernelFunction("stream_chat_tooling_phase")]
    public async Task RunAsync(string correlationId, Kernel kernel)
    {
        var reg = kernel.Services.GetService<SkStreamChatToolingProcessRegistry>();
        if (reg == null)
            return;
        await reg.ExecuteToolingPhaseAsync(correlationId).ConfigureAwait(false);
    }
}
#pragma warning restore SKEXP0080
