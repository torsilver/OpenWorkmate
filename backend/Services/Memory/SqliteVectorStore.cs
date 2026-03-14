using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace OfficeCopilot.Server.Services.Memory;

/// <summary>SQLite 持久化向量存储；向量存为 BLOB，检索时加载候选后算余弦相似度。适合万级以内。</summary>
public sealed class SqliteVectorStore : IVectorStore
{
    private readonly string _connectionString;
    private readonly object _initLock = new();
    private bool _initialized;

    public SqliteVectorStore(string connectionString)
    {
        _connectionString = connectionString ?? "Data Source=:memory:";
    }

    private void EnsureSchema()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS vectors (
                        id TEXT PRIMARY KEY,
                        text TEXT NOT NULL,
                        vector_blob BLOB NOT NULL,
                        session_id TEXT,
                        collection TEXT,
                        created_at INTEGER NOT NULL,
                        metadata TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_vectors_session ON vectors(session_id);
                    CREATE INDEX IF NOT EXISTS idx_vectors_collection ON vectors(collection);
                    """;
                cmd.ExecuteNonQuery();
            }
            _initialized = true;
        }
    }

    private static byte[] VectorToBlob(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToVector(byte[] b)
    {
        var n = b.Length / sizeof(float);
        var v = new float[n];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    public Task UpsertAsync(string id, string text, float[] vector, string? sessionId, IReadOnlyDictionary<string, string>? metadata, string? collection = null, CancellationToken ct = default)
    {
        EnsureSchema();
        var createdAt = DateTime.UtcNow;
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Preserve created_at on update: read existing first
        long createdAtUnix = new DateTimeOffset(createdAt).ToUnixTimeSeconds();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT created_at FROM vectors WHERE id = $id";
            sel.Parameters.AddWithValue("$id", id);
            var existing = sel.ExecuteScalar();
            if (existing != null && existing != DBNull.Value)
                createdAtUnix = Convert.ToInt64(existing);
        }
        cmd.CommandText = """
            INSERT INTO vectors (id, text, vector_blob, session_id, collection, created_at, metadata)
            VALUES ($id, $text, $blob, $sid, $coll, $at, $meta)
            ON CONFLICT(id) DO UPDATE SET
                text = $text, vector_blob = $blob, session_id = $sid, collection = $coll, metadata = $meta
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$blob", VectorToBlob(vector));
        cmd.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coll", (object?)collection ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", createdAtUnix);
        cmd.Parameters.AddWithValue("$meta", metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchAsync(float[] queryVector, int topK, string? sessionIdFilter, string? collectionFilter = null, CancellationToken ct = default)
    {
        EnsureSchema();
        var list = new List<(string Id, string Text, float[] Vector)>();
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            var sql = "SELECT id, text, vector_blob FROM vectors WHERE 1=1";
            if (!string.IsNullOrEmpty(sessionIdFilter)) sql += " AND session_id = $sid";
            if (!string.IsNullOrEmpty(collectionFilter)) sql += " AND collection = $coll";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (!string.IsNullOrEmpty(sessionIdFilter)) cmd.Parameters.AddWithValue("$sid", sessionIdFilter);
            if (!string.IsNullOrEmpty(collectionFilter)) cmd.Parameters.AddWithValue("$coll", collectionFilter);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = r.GetString(0);
                var text = r.GetString(1);
                var blobLen = (int)r.GetBytes(2, 0, null, 0, 0);
                var blob = new byte[blobLen];
                r.GetBytes(2, 0, blob, 0, blobLen);
                list.Add((id, text, BlobToVector(blob)));
            }
        }
        var withScore = list
            .Select(x => (x.Id, x.Text, Score: CosineSimilarity(queryVector, x.Vector)))
            .OrderByDescending(t => t.Score)
            .Take(topK)
            .ToList();
        return withScore;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vectors WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var n = cmd.ExecuteNonQuery();
        return Task.FromResult(n > 0);
    }

    public async Task<(string Text, string? SessionId, DateTime CreatedAt, IReadOnlyDictionary<string, string>? Metadata)?> GetAsync(string id, CancellationToken ct = default)
    {
        EnsureSchema();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT text, session_id, created_at, metadata FROM vectors WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        var text = r.GetString(0);
        var sessionId = r.IsDBNull(1) ? null : r.GetString(1);
        var at = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)).UtcDateTime;
        IReadOnlyDictionary<string, string>? meta = null;
        if (!r.IsDBNull(3))
        {
            var json = r.GetString(3);
            meta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        return (text, sessionId, at, meta);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(string? sessionIdFilter, int skip, int take, string? collectionFilter = null, CancellationToken ct = default)
    {
        EnsureSchema();
        var sql = "SELECT id, text, session_id, created_at, metadata FROM vectors WHERE 1=1";
        if (!string.IsNullOrEmpty(sessionIdFilter)) sql += " AND session_id = $sid";
        if (!string.IsNullOrEmpty(collectionFilter)) sql += " AND collection = $coll";
        sql += " ORDER BY created_at DESC LIMIT $take OFFSET $skip";
        var results = new List<MemoryRecord>();
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (!string.IsNullOrEmpty(sessionIdFilter)) cmd.Parameters.AddWithValue("$sid", sessionIdFilter);
            if (!string.IsNullOrEmpty(collectionFilter)) cmd.Parameters.AddWithValue("$coll", collectionFilter);
            cmd.Parameters.AddWithValue("$take", take);
            cmd.Parameters.AddWithValue("$skip", skip);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                IReadOnlyDictionary<string, string>? meta = null;
                if (!r.IsDBNull(4))
                    meta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4));
                results.Add(new MemoryRecord
                {
                    Id = r.GetString(0),
                    Text = r.GetString(1),
                    SessionId = r.IsDBNull(2) ? null : r.GetString(2),
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)).UtcDateTime,
                    Metadata = meta
                });
            }
        }
        return results;
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
