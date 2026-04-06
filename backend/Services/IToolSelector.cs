using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>两阶段工具选择结果：<see cref="SelectedPairs"/> 为 null 表示使用全量工具。</summary>
public sealed record ToolSelectionOutcome(
    IReadOnlyList<(string PluginName, string FunctionName)>? SelectedPairs,
    string ReasonCode,
    IReadOnlyList<string>? SelectedSubcategoryIds,
    int CandidateFunctionCount,
    int MergedFunctionCount);

/// <summary>两阶段工具选择的可选上下文（向量仅作 stage1 提示，不单独决定工具集）。</summary>
public sealed record ToolSelectionContext(
    string? VectorToolHint = null,
    string? ClientType = null);

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
}
