using System.Collections.Concurrent;

namespace OpenWorkmate.Server.Services.Memory;

/// <summary>内存向量存储，使用余弦相似度检索；进程重启后数据丢失。</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, StoredItem> _items = new();

    public bool IsPersistent => false;

    private sealed class StoredItem
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public float[] Vector { get; set; } = Array.Empty<float>();
        public string? SessionId { get; set; }
        public string? Collection { get; set; }
        public DateTime CreatedAt { get; set; }
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }
        public string? ToolSource { get; set; }
    }

    public Task UpsertAsync(string id, string text, float[] vector, string? sessionId, IReadOnlyDictionary<string, string>? metadata, string? collection = null, string? toolSource = null, CancellationToken ct = default)
    {
        var createdAt = _items.TryGetValue(id, out var existing) ? existing.CreatedAt : DateTime.UtcNow;
        _items[id] = new StoredItem
        {
            Id = id,
            Text = text,
            Vector = vector,
            SessionId = sessionId,
            Collection = collection,
            CreatedAt = createdAt,
            Metadata = metadata != null ? new Dictionary<string, string>(metadata) : null,
            ToolSource = toolSource
        };
        return Task.CompletedTask;
    }

    public Task<int> DeleteByToolSourceAsync(string collectionPrefixPattern, string toolSource, CancellationToken ct = default)
    {
        var toRemove = _items.Where(kv => kv.Value.Collection != null && kv.Value.Collection.StartsWith(collectionPrefixPattern, StringComparison.Ordinal) && string.Equals(kv.Value.ToolSource, toolSource, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
        foreach (var id in toRemove)
            _items.TryRemove(id, out _);
        return Task.FromResult(toRemove.Count);
    }

    public Task<IReadOnlyList<string>> ListIdsByCollectionAndToolSourceAsync(string collection, string toolSource, CancellationToken ct = default)
    {
        var ids = _items
            .Where(kv => string.Equals(kv.Value.Collection, collection, StringComparison.Ordinal)
                && string.Equals(kv.Value.ToolSource, toolSource, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchAsync(float[] queryVector, int topK, string? sessionIdFilter, string? collectionFilter = null, CancellationToken ct = default)
    {
        var candidates = _items.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(sessionIdFilter))
            candidates = candidates.Where(x => string.Equals(x.SessionId, sessionIdFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(collectionFilter))
            candidates = candidates.Where(x => string.Equals(x.Collection, collectionFilter, StringComparison.OrdinalIgnoreCase));

        var withScore = candidates
            .Select(x => (x.Id, x.Text, Score: CosineSimilarity(queryVector, x.Vector)))
            .OrderByDescending(t => t.Score)
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<(string Id, string Text, double Score)>>(withScore);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        return Task.FromResult(_items.TryRemove(id, out _));
    }

    public Task<(string Text, string? SessionId, DateTime CreatedAt, IReadOnlyDictionary<string, string>? Metadata)?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!_items.TryGetValue(id, out var item))
            return Task.FromResult<(string, string?, DateTime, IReadOnlyDictionary<string, string>?)?>(null);
        return Task.FromResult<(string, string?, DateTime, IReadOnlyDictionary<string, string>?)?>((item.Text, item.SessionId, item.CreatedAt, item.Metadata));
    }

    public Task<IReadOnlyList<MemoryRecord>> ListAsync(string? sessionIdFilter, int skip, int take, string? collectionFilter = null, string? agentNameFilter = null, CancellationToken ct = default)
    {
        var query = _items.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(sessionIdFilter))
            query = query.Where(x => string.Equals(x.SessionId, sessionIdFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(collectionFilter))
            query = query.Where(x => string.Equals(x.Collection, collectionFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(agentNameFilter))
        {
            var name = agentNameFilter.Trim();
            query = query.Where(x => x.Metadata != null && x.Metadata.TryGetValue("agentName", out var an) && string.Equals(an, name, StringComparison.OrdinalIgnoreCase));
        }
        var list = query
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(x =>
            {
                var agentName = x.Metadata != null && x.Metadata.TryGetValue("agentName", out var an) ? an : null;
                return new MemoryRecord
                {
                    Id = x.Id,
                    Text = x.Text,
                    SessionId = x.SessionId,
                    AgentName = agentName,
                    CreatedAt = x.CreatedAt,
                    Metadata = x.Metadata
                };
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(list);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length != a.Length) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom <= 0 ? 0 : dot / denom;
    }
}
