using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Services;

/// <summary>工具索引构建模式：仅内置插件、仅用户插件（UserSkill/MCP）、或全部（兼容旧用）。</summary>
public enum ToolIndexBuildMode
{
    /// <summary>只构建内置插件索引，写前删除现有 tool_source=builtin；用于 --build-tool-index。</summary>
    BuiltinOnly,
    /// <summary>只构建用户 Skill/MCP 索引，写前删除现有 tool_source=user；用于运行时。</summary>
    UserOnly,
    /// <summary>构建全部（内置+用户），不按 tool_source 删除；兼容旧行为。</summary>
    All
}

/// <summary>
/// 工具向量索引：按 clientType 分 collection 存储工具描述，支持按用户输入检索候选工具；检索结果足够好时可直接用作工具集，否则回退两轮选择。
/// </summary>
public interface IToolIndexService
{
    /// <summary>
    /// 使用当前 Kernel 的插件/函数列表，按端（clientType）分别写入工具向量索引。仅对 ClientTypeToolFilter.IsAllowed 允许的工具写入对应端的 collection。
    /// </summary>
    /// <param name="mode">BuiltinOnly 只写内置并标 tool_source=builtin；UserOnly 只写用户工具并标 tool_source=user；All 写全部（不标 tool_source，兼容）。</param>
    Task BuildIndexAsync(Kernel kernel, ToolIndexBuildMode mode = ToolIndexBuildMode.UserOnly, CancellationToken ct = default);

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
