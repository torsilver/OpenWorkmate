namespace OfficeCopilot.Server.Services.Memory;

/// <summary>向量存储：仅负责向量与元数据的存取，不负责文本向量化。collection 区分记忆(memory)与知识库(kb:id)。</summary>
public interface IVectorStore
{
    /// <summary>是否持久化；为 false 时（如 InMemory）工具索引不构建，工具选择走两轮对话。</summary>
    bool IsPersistent { get; }

    Task UpsertAsync(string id, string text, float[] vector, string? sessionId, IReadOnlyDictionary<string, string>? metadata, string? collection = null, string? toolSource = null, CancellationToken ct = default);
    Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchAsync(float[] queryVector, int topK, string? sessionIdFilter, string? collectionFilter = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<(string Text, string? SessionId, DateTime CreatedAt, IReadOnlyDictionary<string, string>? Metadata)?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> ListAsync(string? sessionIdFilter, int skip, int take, string? collectionFilter = null, string? agentNameFilter = null, CancellationToken ct = default);

    /// <summary>删除指定 collection 前缀且 tool_source 匹配的向量（用于清理内置/用户工具索引）。</summary>
    Task<int> DeleteByToolSourceAsync(string collectionPrefixPattern, string toolSource, CancellationToken ct = default);
}
