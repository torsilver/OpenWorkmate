using Microsoft.SemanticKernel.ChatCompletion;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 根据用户消息与可选历史，选出本轮应参与的工具（插件）名集合，用于按需只传部分工具 schema 给模型。
/// </summary>
public interface IToolSelector
{
    /// <summary>
    /// 从当前消息（及可选最近历史）中选出可能用到的插件名。
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
}
