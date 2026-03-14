namespace OfficeCopilot.Server.Services.Memory;

/// <summary>记忆存储服务：封装嵌入 + 向量存储，提供增删改查。</summary>
public interface IMemoryStoreService
{
    /// <summary>是否可用（已配置嵌入且存储就绪）。</summary>
    bool IsAvailable { get; }
    /// <summary>保存一条记忆；若 id 已存在则更新（会重新向量化）。</summary>
    Task<string> SaveAsync(string? id, string text, string? sessionId, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default);
    /// <summary>按查询文本检索最相关的 topK 条记忆。</summary>
    Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchAsync(string query, int topK, string? sessionIdFilter, CancellationToken ct = default);
    /// <summary>删除一条记忆。</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    /// <summary>获取单条记忆（不包含向量）。</summary>
    Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>分页列出记忆。</summary>
    Task<IReadOnlyList<MemoryRecord>> ListAsync(string? sessionIdFilter, int skip, int take, CancellationToken ct = default);
    /// <summary>向知识库追加一条分块文本（用于 RAG 摄入）。</summary>
    Task AddChunkToKnowledgeBaseAsync(string knowledgeBaseId, string chunkId, string text, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default);
    /// <summary>在指定知识库内检索与 query 最相关的 topK 条。</summary>
    Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchKnowledgeBaseAsync(string knowledgeBaseId, string query, int topK, CancellationToken ct = default);
}
