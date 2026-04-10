using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>主会话单轮动态工具状态：由外层 MAF 循环写入；<see cref="AgentToolingPlugin"/> 经 AsyncLocal 读取。</summary>
public sealed class DynamicToolingTurnState
{
    public DynamicToolingTurnState(
        DynamicToolingConfig config,
        ToolCatalogIndex catalog,
        IReadOnlyCollection<string>? bootstrapFunctionNames = null)
    {
        Config = config;
        Catalog = catalog;
        BootstrapFunctionNames = bootstrapFunctionNames is { Count: > 0 }
            ? new HashSet<string>(bootstrapFunctionNames, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public DynamicToolingConfig Config { get; }
    public ToolCatalogIndex Catalog { get; }

    /// <summary>与 <see cref="SessionToolResolver.BuildDynamicActiveToolList"/> 的 mergePlan 一致（工具阶段写入）。</summary>
    public bool MergePlanIntoDynamicBootstrap { get; set; }

    /// <summary>构建允许列表与动态工具表时用，与当前会话一致。</summary>
    public string? ClientTypeForTools { get; set; }

    /// <summary>构建允许列表与动态工具表时用，与当前会话一致。</summary>
    public string? SessionIdForTools { get; set; }

    /// <summary>
    /// 当前 MAF pass 绑定到 <see cref="Microsoft.Extensions.AI.ChatOptions.Tools"/> 的列表引用；
    /// <c>activate_tools</c> 成功后原地替换内容，使同一次 <c>RunStreamingAsync</c> 内后续模型请求带上新工具。
    /// </summary>
    public List<AITool>? ToolListMutationTarget { get; set; }

    /// <summary>本轮动态工具 bootstrap 中的函数名（用于空关键词检索时优先列出）。</summary>
    public HashSet<string> BootstrapFunctionNames { get; }

    /// <summary>已成功激活的业务工具函数名（不含 search/activate）。</summary>
    public HashSet<string> ActivatedFunctionNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int SearchInvocationCount { get; set; }
    public int ActivateInvocationCount { get; set; }

    /// <summary>上一外层 pass 中 <c>activate_tools</c> 是否新加入了至少一个工具。</summary>
    public bool ExpansionOccurredInLastPass { get; set; }

    public void MarkExpansion() => ExpansionOccurredInLastPass = true;

    public void ClearExpansionFlag() => ExpansionOccurredInLastPass = false;

    public bool HasActivatedAnyBusinessTool()
    {
        foreach (var n in ActivatedFunctionNames)
        {
            if (!DynamicToolingConstants.MetaFunctionNames.Contains(n))
                return true;
        }
        return false;
    }
}

/// <summary>将当前轮的 <see cref="DynamicToolingTurnState"/> 绑定到异步流（插件内读取）。</summary>
public static class DynamicToolingTurnScope
{
    private static readonly AsyncLocal<DynamicToolingTurnState?> Slot = new();

    public static DynamicToolingTurnState? Current => Slot.Value;

    public static IDisposable Push(DynamicToolingTurnState state)
    {
        var prior = Slot.Value;
        Slot.Value = state;
        return new PopDisposable(prior);
    }

    private sealed class PopDisposable : IDisposable
    {
        private readonly DynamicToolingTurnState? _prior;
        private bool _disposed;

        public PopDisposable(DynamicToolingTurnState? prior) => _prior = prior;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Slot.Value = _prior;
        }
    }
}
