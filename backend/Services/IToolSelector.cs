using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

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
    /// <summary>
    /// 从当前消息（及可选最近历史）中选出可能用到的插件名。用于单阶段模式。
    /// </summary>
    /// <param name="userMessage">当前用户消息（含可能的系统提示前缀）</param>
    /// <param name="recentHistory">可选的一轮最近历史，用于上下文</param>
    /// <param name="availablePluginNames">当前 Kernel 中已注册的插件名列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>应参与本轮的插件名集合（含 AlwaysIncludePlugins），可为空表示“不限制”即使用全量工具</returns>
    Task<IReadOnlyList<string>> SelectPluginNamesAsync(
        string userMessage,
        ChatHistory? recentHistory,
        IReadOnlyList<string> availablePluginNames,
        CancellationToken ct = default);

    /// <summary>
    /// 两阶段选择：先选子类再合并函数。<see cref="ToolSelectionOutcome.SelectedPairs"/> 为 null 表示使用全量工具。
    /// </summary>
    Task<ToolSelectionOutcome> SelectFunctionsAsync(
        string userMessage,
        ChatHistory? recentHistory,
        Kernel kernel,
        CancellationToken ct = default,
        ToolSelectionContext? context = null);
}
