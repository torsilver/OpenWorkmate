using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 提供当前 Kernel 与 ActiveModelId 的访问，供 ToolSelectionService 在 LLM 模式下按 serviceId 获取 IChatCompletionService。
/// 由 ChatService 在 RebuildKernelAsync 后更新。
/// </summary>
public interface IKernelAccessor
{
    Kernel? Kernel { get; }
    string ActiveModelId { get; }
    void Set(Kernel kernel, string activeModelId);
}

/// <inheritdoc />
public sealed class KernelAccessor : IKernelAccessor
{
    private volatile Kernel? _kernel;
    private volatile string _activeModelId = "";

    public Kernel? Kernel => _kernel;
    public string ActiveModelId => _activeModelId;

    public void Set(Kernel kernel, string activeModelId)
    {
        _kernel = kernel;
        _activeModelId = activeModelId ?? "";
    }
}
