using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>两阶段工具选择结果：<see cref="SelectedPairs"/> 为 null 表示使用全量工具。</summary>
public sealed record ToolSelectionOutcome(
    IReadOnlyList<(string PluginName, string FunctionName)>? SelectedPairs,
    string ReasonCode,
    IReadOnlyList<string>? SelectedSubcategoryIds,
    int CandidateFunctionCount,
    int MergedFunctionCount);

/// <summary>两阶段工具选择的可选上下文（如按客户端隐藏 CurrentDocument 子类等）。</summary>
public sealed record ToolSelectionContext(string? ClientType = null);

/// <summary>工具需求门控结果：<see cref="BindTools"/> 为 false 时主会话不绑定任何工具（闲聊路径）。</summary>
/// <param name="InvokedLlm">本次是否实际调用了主模型做门控（配置关闭或无客户端时为 false）。</param>
public sealed record ToolNeedGateResult(bool BindTools, string TraceDetail, bool InvokedLlm = false);

/// <summary>
/// 根据用户消息与可选历史，选出本轮应参与的工具（插件名或具体函数），用于按需只传部分工具 schema 给模型。
/// </summary>
public interface IToolSelector
{
    Task<IReadOnlyList<string>> SelectPluginNamesAsync(
        string userMessage,
        IReadOnlyList<ChatMessage>? recentHistory,
        IReadOnlyList<string> availablePluginNames,
        CancellationToken ct = default);

    /// <summary>
    /// 两阶段选择：先选子类再合并函数。<see cref="ToolSelectionOutcome.SelectedPairs"/> 为 null 表示使用全量工具。
    /// </summary>
    Task<ToolSelectionOutcome> SelectFunctionsAsync(
        string userMessage,
        IReadOnlyList<ChatMessage>? recentHistory,
        ToolRegistry toolRegistry,
        CancellationToken ct = default,
        ToolSelectionContext? context = null);

    /// <summary>
    /// 工具需求门控：判断本轮是否应绑定工具。配置关闭时返回 <see cref="ToolNeedGateResult.BindTools"/> true 且不调用模型。
    /// 解析失败或空输出时保守为需要工具。
    /// </summary>
    Task<ToolNeedGateResult> EvaluateToolNeedGateAsync(
        string userMessage,
        IReadOnlyList<ChatMessage>? recentHistory,
        CancellationToken ct = default);
}
