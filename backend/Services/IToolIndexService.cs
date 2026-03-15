using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 工具向量索引：按 clientType 分 collection 存储工具描述，支持按用户输入检索候选工具；检索结果足够好时可直接用作工具集，否则回退两轮选择。
/// </summary>
public interface IToolIndexService
{
    /// <summary>
    /// 使用当前 Kernel 的插件/函数列表，按端（clientType）分别写入工具向量索引。仅对 ClientTypeToolFilter.IsAllowed 允许的工具写入对应端的 collection。
    /// </summary>
    Task BuildIndexAsync(Kernel kernel, CancellationToken ct = default);

    /// <summary>
    /// 按用户查询在指定端的工具 collection 中检索，返回候选 (PluginName, FunctionName) 及是否「足够好」。
    /// </summary>
    /// <param name="userQuery">用户消息（可含简短最近历史）</param>
    /// <param name="clientType">当前端，空视为 chrome</param>
    /// <param name="topK">返回条数上限</param>
    /// <param name="minScore">最低分数阈值，用于判定足够好</param>
    /// <param name="minCount">最少条数，用于判定足够好</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>候选列表（可能为空）；GoodEnough 表示是否满足 minCount 且最高分 ≥ minScore</returns>
    Task<(IReadOnlyList<(string PluginName, string FunctionName)> Results, bool GoodEnough)> SearchToolsAsync(
        string userQuery,
        string? clientType,
        int topK = 20,
        double minScore = 0.7,
        int minCount = 1,
        CancellationToken ct = default);
}
