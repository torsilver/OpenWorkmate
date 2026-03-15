namespace OfficeCopilot.Server.Services.Memory;

/// <summary>向量存储：仅负责向量与元数据的存取，不负责文本向量化。collection 区分记忆(memory)与知识库(kb:id)。</summary>
public interface IVectorStore
{
    Task UpsertAsync(string id, string text, float[] vector, string? sessionId, IReadOnlyDictionary<string, string>? metadata, string? collection = null, CancellationToken ct = default);
    Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchAsync(float[] queryVector, int topK, string? sessionIdFilter, string? collectionFilter = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<(string Text, string? SessionId, DateTime CreatedAt, IReadOnlyDictionary<string, string>? Metadata)?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> ListAsync(string? sessionIdFilter, int skip, int take, string? collectionFilter = null, string? agentNameFilter = null, CancellationToken ct = default);
}
