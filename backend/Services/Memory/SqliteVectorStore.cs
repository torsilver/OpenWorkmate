using Microsoft.Data.Sqlite;

namespace OpenWorkmate.Server.Services.Memory;

/// <summary>SQLite 持久化向量存储；向量存为 BLOB，检索时加载候选后算余弦相似度。适合万级以内。</summary>
public sealed class SqliteVectorStore : IVectorStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public bool IsPersistent => true;

    public SqliteVectorStore(string connectionString)
    {
        _connectionString = connectionString ?? "Data Source=:memory:";
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS vectors (
                        id TEXT PRIMARY KEY,
                        text TEXT NOT NULL,
                        vector_blob BLOB NOT NULL,
                        session_id TEXT,
                        collection TEXT,
                        created_at INTEGER NOT NULL,
                        metadata TEXT,
                        tool_source TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_vectors_session ON vectors(session_id);
                    CREATE INDEX IF NOT EXISTS idx_vectors_collection ON vectors(collection);
                    """;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('vectors') WHERE name='tool_source'";
                var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var hasToolSource = scalar != null && scalar != DBNull.Value && Convert.ToInt64(scalar) > 0;
                if (!hasToolSource)
                {
                    cmd.CommandText = "ALTER TABLE vectors ADD COLUMN tool_source TEXT";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
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

    public async Task UpsertAsync(string id, string text, float[] vector, string? sessionId, IReadOnlyDictionary<string, string>? metadata, string? collection = null, string? toolSource = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var createdAt = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        long createdAtUnix = new DateTimeOffset(createdAt).ToUnixTimeSeconds();
        await using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT created_at FROM vectors WHERE id = $id";
            sel.Parameters.AddWithValue("$id", id);
            var existing = await sel.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (existing != null && existing != DBNull.Value)
                createdAtUnix = Convert.ToInt64(existing);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vectors (id, text, vector_blob, session_id, collection, created_at, metadata, tool_source)
            VALUES ($id, $text, $blob, $sid, $coll, $at, $meta, $ts)
            ON CONFLICT(id) DO UPDATE SET
                text = $text, vector_blob = $blob, session_id = $sid, collection = $coll, metadata = $meta, tool_source = $ts
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$blob", VectorToBlob(vector));
        cmd.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coll", (object?)collection ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", createdAtUnix);
        cmd.Parameters.AddWithValue("$meta", metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", (object?)toolSource ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Id, string Text, double Score)>> SearchAsync(float[] queryVector, int topK, string? sessionIdFilter, string? collectionFilter = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var list = new List<(string Id, string Text, float[] Vector)>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var sql = "SELECT id, text, vector_blob FROM vectors WHERE 1=1";
        if (!string.IsNullOrEmpty(sessionIdFilter)) sql += " AND session_id = $sid";
        if (!string.IsNullOrEmpty(collectionFilter)) sql += " AND collection = $coll";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (!string.IsNullOrEmpty(sessionIdFilter)) cmd.Parameters.AddWithValue("$sid", sessionIdFilter);
        if (!string.IsNullOrEmpty(collectionFilter)) cmd.Parameters.AddWithValue("$coll", collectionFilter);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = r.GetString(0);
            var text = r.GetString(1);
            var blobLen = (int)r.GetBytes(2, 0, null, 0, 0);
            var blob = new byte[blobLen];
            r.GetBytes(2, 0, blob, 0, blobLen);
            list.Add((id, text, BlobToVector(blob)));
        }

        var withScore = list
            .Select(x => (x.Id, x.Text, Score: CosineSimilarity(queryVector, x.Vector)))
            .OrderByDescending(t => t.Score)
            .Take(topK)
            .ToList();
        return withScore;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vectors WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return n > 0;
    }

    public async Task<(string Text, string? SessionId, DateTime CreatedAt, IReadOnlyDictionary<string, string>? Metadata)?> GetAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT text, session_id, created_at, metadata FROM vectors WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        var text = r.GetString(0);
        var sessionId = await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetString(1);
        var at = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)).UtcDateTime;
        IReadOnlyDictionary<string, string>? meta = null;
        if (!await r.IsDBNullAsync(3, ct).ConfigureAwait(false))
        {
            var json = r.GetString(3);
            meta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }

        return (text, sessionId, at, meta);
    }

    public async Task<int> DeleteByToolSourceAsync(string collectionPrefixPattern, string toolSource, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vectors WHERE collection LIKE $patt AND tool_source = $ts";
        cmd.Parameters.AddWithValue("$patt", collectionPrefixPattern + "%");
        cmd.Parameters.AddWithValue("$ts", toolSource);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListIdsByCollectionAndToolSourceAsync(string collection, string toolSource, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM vectors WHERE collection = $coll AND tool_source = $ts";
        cmd.Parameters.AddWithValue("$coll", collection);
        cmd.Parameters.AddWithValue("$ts", toolSource);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(r.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(string? sessionIdFilter, int skip, int take, string? collectionFilter = null, string? agentNameFilter = null, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var sql = "SELECT id, text, session_id, created_at, metadata FROM vectors WHERE 1=1";
        if (!string.IsNullOrEmpty(sessionIdFilter)) sql += " AND session_id = $sid";
        if (!string.IsNullOrEmpty(collectionFilter)) sql += " AND collection = $coll";
        if (!string.IsNullOrEmpty(agentNameFilter)) sql += " AND json_extract(metadata, '$.agentName') = $agentName";
        sql += " ORDER BY created_at DESC LIMIT $take OFFSET $skip";
        var results = new List<MemoryRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (!string.IsNullOrEmpty(sessionIdFilter)) cmd.Parameters.AddWithValue("$sid", sessionIdFilter);
        if (!string.IsNullOrEmpty(collectionFilter)) cmd.Parameters.AddWithValue("$coll", collectionFilter);
        if (!string.IsNullOrEmpty(agentNameFilter)) cmd.Parameters.AddWithValue("$agentName", agentNameFilter.Trim());
        cmd.Parameters.AddWithValue("$take", take);
        cmd.Parameters.AddWithValue("$skip", skip);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            IReadOnlyDictionary<string, string>? meta = null;
            string? agentName = null;
            if (!await r.IsDBNullAsync(4, ct).ConfigureAwait(false))
            {
                meta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4));
                if (meta != null && meta.TryGetValue("agentName", out var an)) agentName = an;
            }

            results.Add(new MemoryRecord
            {
                Id = r.GetString(0),
                Text = r.GetString(1),
                SessionId = await r.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : r.GetString(2),
                AgentName = agentName,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)).UtcDateTime,
                Metadata = meta
            });
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
