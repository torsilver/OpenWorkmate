namespace OfficeCopilot.Server.Services;

/// <summary>工具向量检索结果：去重后的命中（按分数降序）、是否足够好、带分数列表（供 agent_trace）。</summary>
public sealed record ToolSearchResult(
    IReadOnlyList<(string PluginName, string FunctionName)> Results,
    bool GoodEnough,
    IReadOnlyList<(string PluginName, string FunctionName, double Score)> ScoredHits);

/// <summary>工具索引构建模式：仅内置插件、仅用户插件（UserSkill/MCP）、或全部（兼容旧用）。</summary>
public enum ToolIndexBuildMode
{
    /// <summary>只构建内置插件索引，写前删除现有 tool_source=builtin；用于 --build-tool-index。</summary>
    BuiltinOnly,
    /// <summary>只构建用户 Skill/MCP 索引，写前删除现有 tool_source=user；兼容/运维全量重刷。正常运行时用 SyncUserToolIndexAsync 增量同步。</summary>
    UserOnly,
    /// <summary>构建全部（内置+用户），不按 tool_source 删除；兼容旧行为。</summary>
    All
}

/// <summary>
/// 工具向量索引：按 clientType 分 collection 存储工具描述，支持按用户输入检索候选工具；命中分数用于遥测与两阶段 stage1 参考，最终工具集由子类 LLM 选择决定。
/// </summary>
public interface IToolIndexService
{
    /// <summary>
    /// 使用 ToolRegistry 中的工具列表，按端（clientType）分别写入工具向量索引。
    /// </summary>
    Task BuildIndexAsync(ToolRegistry toolRegistry, ToolIndexBuildMode mode = ToolIndexBuildMode.UserOnly, CancellationToken ct = default);

    /// <summary>
    /// 增量同步用户工具索引（UserSkill / MCP，不含 STT/OCR）：仅对缺失或描述文本变化的条目 embedding；删除已从 ToolRegistry 消失的 user 工具向量。
    /// </summary>
    Task SyncUserToolIndexAsync(ToolRegistry toolRegistry, CancellationToken ct = default);

    /// <summary>
    /// 按用户查询在指定端的工具 collection 中检索，返回候选 (PluginName, FunctionName) 及是否「足够好」。
    /// </summary>
    Task<ToolSearchResult> SearchToolsAsync(
        string userQuery,
        string? clientType,
        int topK = 20,
        double minScore = 0.7,
        int minCount = 1,
        CancellationToken ct = default);
}
