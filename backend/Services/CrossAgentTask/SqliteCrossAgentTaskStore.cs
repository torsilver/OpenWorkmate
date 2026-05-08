using Microsoft.Data.Sqlite;

namespace OpenWorkmate.Server.Services.CrossAgentTask;

/// <summary>SQLite 持久化跨 Agent 任务。</summary>
public sealed class SqliteCrossAgentTaskStore : ICrossAgentTaskStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public SqliteCrossAgentTaskStore(string connectionString)
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
                    CREATE TABLE IF NOT EXISTS cross_agent_tasks (
                        id TEXT PRIMARY KEY,
                        from_session_id TEXT NOT NULL,
                        target_client_type TEXT,
                        target_session_id TEXT,
                        description TEXT NOT NULL,
                        status TEXT NOT NULL DEFAULT 'pending',
                        claimed_by TEXT,
                        result_summary TEXT,
                        created_at INTEGER NOT NULL,
                        completed_at INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS idx_cross_agent_tasks_status ON cross_agent_tasks(status);
                    CREATE INDEX IF NOT EXISTS idx_cross_agent_tasks_target ON cross_agent_tasks(target_client_type, target_session_id);
                    """;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            try
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE cross_agent_tasks ADD COLUMN claimed_by TEXT";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                /* column already exists */
            }

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<CrossAgentTaskItem> AddAsync(string fromSessionId, string? targetClientType, string? targetSessionId, string description, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var item = new CrossAgentTaskItem
        {
            Id = id,
            FromSessionId = fromSessionId ?? "",
            TargetClientType = string.IsNullOrWhiteSpace(targetClientType) ? null : targetClientType.Trim(),
            TargetSessionId = string.IsNullOrWhiteSpace(targetSessionId) ? null : targetSessionId.Trim(),
            Description = description?.Trim() ?? "",
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cross_agent_tasks (id, from_session_id, target_client_type, target_session_id, description, status, created_at)
            VALUES ($id, $from, $tct, $tsid, $desc, 'pending', $at)
            """;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$from", item.FromSessionId);
        cmd.Parameters.AddWithValue("$tct", (object?)item.TargetClientType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tsid", (object?)item.TargetSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", item.Description);
        cmd.Parameters.AddWithValue("$at", new DateTimeOffset(item.CreatedAt).ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<CrossAgentTaskItem>> GetPendingForTargetAsync(string? clientType, string? sessionId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var list = new List<CrossAgentTaskItem>();
        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(clientType))
            return list;
        var sid = sessionId ?? "";
        var tct = clientType?.Trim() ?? "";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var claimCmd = conn.CreateCommand())
        {
            claimCmd.CommandText = """
                UPDATE cross_agent_tasks SET claimed_by = $sid
                WHERE status = 'pending' AND (claimed_by IS NULL OR claimed_by = '') AND (
                    (target_session_id IS NOT NULL AND target_session_id = $tsid)
                    OR (target_session_id IS NULL AND target_client_type = $tct)
                )
                """;
            claimCmd.Parameters.AddWithValue("$sid", sid);
            claimCmd.Parameters.AddWithValue("$tsid", sid);
            claimCmd.Parameters.AddWithValue("$tct", tct);
            await claimCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, from_session_id, target_client_type, target_session_id, description, status, result_summary, created_at, completed_at
            FROM cross_agent_tasks WHERE status = 'pending' AND claimed_by = $sid
            ORDER BY created_at ASC
            """;
        cmd.Parameters.AddWithValue("$sid", sid);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(await ReadRowAsync(r, ct).ConfigureAwait(false));
        return list;
    }

    public async Task<CrossAgentTaskItem?> GetAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, from_session_id, target_client_type, target_session_id, description, status, result_summary, created_at, completed_at FROM cross_agent_tasks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return await ReadRowAsync(r, ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateStatusAsync(string id, string status, string? resultSummary, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE cross_agent_tasks SET status = $status, result_summary = $summary, completed_at = $completed WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status?.Trim() ?? "done");
        cmd.Parameters.AddWithValue("$summary", (object?)resultSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", (status == "done" || status == "failed") ? new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() : DBNull.Value);
        var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return n > 0;
    }

    private static async Task<CrossAgentTaskItem> ReadRowAsync(SqliteDataReader r, CancellationToken ct)
    {
        var targetClientType = await r.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : r.GetString(2);
        var targetSessionId = await r.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : r.GetString(3);
        var resultSummary = await r.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : r.GetString(6);
        DateTime? completedAt = await r.IsDBNullAsync(8, ct).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)).UtcDateTime;
        return new CrossAgentTaskItem
        {
            Id = r.GetString(0),
            FromSessionId = r.GetString(1),
            TargetClientType = targetClientType,
            TargetSessionId = targetSessionId,
            Description = r.GetString(4),
            Status = r.GetString(5),
            ResultSummary = resultSummary,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)).UtcDateTime,
            CompletedAt = completedAt
        };
    }
}
