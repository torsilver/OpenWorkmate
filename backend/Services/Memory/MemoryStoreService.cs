namespace OfficeCopilot.Server.Services.Memory;

/// <summary>记忆存储服务：使用 IEmbeddingProvider 生成向量并写入 IVectorStore。</summary>
public sealed class MemoryStoreService : IMemoryStoreService
{
    private readonly IEmbeddingProvider _embedding;
    private readonly IVectorStore _store;

    public MemoryStoreService(IEmbeddingProvider embedding, IVectorStore store)
    {
        _embedding = embedding;
        _store = store;
    }

    public bool IsAvailable => _embedding.IsConfigured;

    public async Task<string> SaveAsync(string? id, string text, string? sessionId, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        if (!_embedding.IsConfigured || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Embedding is not configured or text is empty.");
        var vector = await _embedding.GenerateEmbeddingAsync(text, ct).ConfigureAwait(false);
        if (vector == null || vector.Length == 0)
            throw new InvalidOperationException("Failed to generate embedding.");
        var key = id ?? Guid.NewGuid().ToString("N");
        await _store.UpsertAsync(key, text, vector, sessionId, metadata, "memory", ct).ConfigureAwait(false);
        return key;
    }

    public async Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchAsync(string query, int topK, string? sessionIdFilter, CancellationToken ct = default)
    {
        if (!_embedding.IsConfigured || string.IsNullOrWhiteSpace(query))
            return Array.Empty<(string, string, double)>();
        var vector = await _embedding.GenerateEmbeddingAsync(query, ct).ConfigureAwait(false);
        if (vector == null || vector.Length == 0)
            return Array.Empty<(string, string, double)>();
        return await _store.SearchAsync(vector, topK, sessionIdFilter, "memory", ct).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => _store.DeleteAsync(id, ct);

    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        var t = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (t == null) return null;
        return new MemoryRecord { Id = id, Text = t.Value.Text, SessionId = t.Value.SessionId, CreatedAt = t.Value.CreatedAt, Metadata = t.Value.Metadata };
    }

    public Task<IReadOnlyList<MemoryRecord>> ListAsync(string? sessionIdFilter, int skip, int take, CancellationToken ct = default)
        => _store.ListAsync(sessionIdFilter, skip, take, "memory", ct);

    public async Task AddChunkToKnowledgeBaseAsync(string knowledgeBaseId, string chunkId, string text, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        if (!_embedding.IsConfigured || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Embedding not configured or text empty.");
        var vector = await _embedding.GenerateEmbeddingAsync(text, ct).ConfigureAwait(false);
        if (vector == null || vector.Length == 0)
            throw new InvalidOperationException("Failed to generate embedding.");
        var coll = "kb:" + (knowledgeBaseId ?? "").Trim();
        await _store.UpsertAsync(chunkId, text, vector, null, metadata, coll, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchKnowledgeBaseAsync(string knowledgeBaseId, string query, int topK, CancellationToken ct = default)
    {
        if (!_embedding.IsConfigured || string.IsNullOrWhiteSpace(query))
            return Array.Empty<(string, string, double)>();
        var vector = await _embedding.GenerateEmbeddingAsync(query, ct).ConfigureAwait(false);
        if (vector == null || vector.Length == 0)
            return Array.Empty<(string, string, double)>();
        var coll = "kb:" + (knowledgeBaseId ?? "").Trim();
        return await _store.SearchAsync(vector, Math.Clamp(topK, 1, 50), null, coll, ct).ConfigureAwait(false);
    }
}
