using Microsoft.Extensions.AI;

namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>
/// 主会话单轮动态工具状态：由外层 MAF 循环写入；<see cref="AgentToolingPlugin"/> 经 AsyncLocal 读取。
/// 生命周期为「单条用户消息」触发的本轮流式编排（<see cref="OpenWorkmate.Server.Services.Chat.StreamChatTurnContext"/> 在 tooling 阶段新建本类型）；
/// 内置 completion verifier 的续跑与首轮共用同一 <see cref="OpenWorkmate.Server.Services.Chat.StreamChatTurnContext"/>，故共用同一实例、不在续跑前清空。
/// </summary>
public sealed class DynamicToolingTurnState
{
    public DynamicToolingTurnState(
        DynamicToolingConfig config,
        ToolCatalogIndex catalog,
        SkillCatalogIndex skillCatalog,
        IReadOnlyList<string>? bootstrapFunctionNamesOrder = null)
    {
        Config = config;
        Catalog = catalog;
        SkillCatalog = skillCatalog;
        if (bootstrapFunctionNamesOrder is { Count: > 0 })
        {
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in bootstrapFunctionNamesOrder)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (!seen.Add(n.Trim())) continue;
                order.Add(n.Trim());
            }
            BootstrapFunctionNamesOrder = order;
            BootstrapFunctionNames = seen;
        }
        else
        {
            BootstrapFunctionNamesOrder = Array.Empty<string>();
            BootstrapFunctionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public DynamicToolingConfig Config { get; }
    public ToolCatalogIndex Catalog { get; }
    public SkillCatalogIndex SkillCatalog { get; }

    /// <summary>本轮 <c>select_skill_for_turn</c> 已选中的技能 Id（canonical，大小写不敏感）。</summary>
    public HashSet<string> SelectedSkillIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int SkillSearchInvocationCount { get; set; }
    public int SkillSelectInvocationCount { get; set; }

    /// <summary>与 <see cref="SessionToolResolver.BuildDynamicActiveToolList"/> 的 mergePlan 一致（工具阶段写入）。</summary>
    public bool MergePlanIntoDynamicBootstrap { get; set; }

    /// <summary>首轮 tooling 时的路由（<see cref="BootstrapFunctionNamesOrder"/> 为空时物化 bootstrap 用）。</summary>
    public TurnRoute InitialTurnRoute { get; set; } = TurnRoute.Standard;

    /// <summary>构建允许列表与动态工具表时用，与当前会话一致。</summary>
    public string? ClientTypeForTools { get; set; }

    /// <summary>构建允许列表与动态工具表时用，与当前会话一致。</summary>
    public string? SessionIdForTools { get; set; }

    /// <summary>WPS 会话 <c>set_context</c> 的宿主快照；供 <c>BuildDynamicActiveToolList</c> 与 <c>activate_tools</c> 刷新工具表时过滤 CurrentDocument。</summary>
    public string? WpsHostKindForTools { get; set; }

    /// <summary>
    /// 当前 MAF pass 绑定到 <see cref="Microsoft.Extensions.AI.ChatOptions.Tools"/> 的列表引用；
    /// <c>activate_tools</c> 成功后原地替换内容，使同一次 <c>RunStreamingAsync</c> 内后续模型请求带上新工具。
    /// </summary>
    public List<AITool>? ToolListMutationTarget { get; set; }

    /// <summary>首轮 bootstrap 函数名顺序（与 <see cref="ChatService.StreamPhases"/> 写入的 Tools 顺序一致）。</summary>
    public IReadOnlyList<string> BootstrapFunctionNamesOrder { get; }

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

    /// <summary>
    /// 本轮是否已执行过至少一次非引导（<see cref="DynamicToolingConstants.MetaFunctionNames"/>）工具；
    /// 用于抑制「未 activate 则全量兜底」的第二 pass，避免脚本等业务工具已成功后再开全量表导致重复操作。
    /// </summary>
    public bool EffectfulNonMetaToolInvokedThisTurn { get; private set; }

    /// <summary>
    /// 在插件已实际执行后调用：meta 工具（检索/激活/技能链引导）不置位。
    /// </summary>
    public void MarkEffectfulNonMetaInvocation(string? bareFunctionName)
    {
        if (string.IsNullOrWhiteSpace(bareFunctionName))
            return;
        if (DynamicToolingConstants.MetaFunctionNames.Contains(bareFunctionName))
            return;
        EffectfulNonMetaToolInvokedThisTurn = true;
    }

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
