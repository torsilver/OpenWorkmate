using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server.Services.Chat;

/// <summary>在目录下使用 <c>chat-sessions.db</c> 存储会话元数据与消息行（WAL，外键开启）。</summary>
public sealed class SqliteChatSessionStore : IChatSessionStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteChatSessionStore> _logger;
    private readonly object _initLock = new();
    private bool _initialized;

    public SqliteChatSessionStore(string databaseDirectory, ILogger<SqliteChatSessionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(databaseDirectory);
        _logger = logger;
        Directory.CreateDirectory(databaseDirectory);
        var dbPath = Path.Combine(databaseDirectory, "chat-sessions.db");
        _connectionString = "Data Source=" + dbPath;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                ApplyPragmas(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS chat_sessions (
                        session_id TEXT NOT NULL PRIMARY KEY,
                        updated_at_utc TEXT NOT NULL,
                        title_preview TEXT NOT NULL DEFAULT '',
                        message_count INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_chat_sessions_updated ON chat_sessions(updated_at_utc DESC);
                    CREATE TABLE IF NOT EXISTS chat_session_messages (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        sort_order INTEGER NOT NULL,
                        role TEXT NOT NULL,
                        text TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        FOREIGN KEY (session_id) REFERENCES chat_sessions(session_id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS idx_csm_session ON chat_session_messages(session_id, sort_order);
                    """;
                cmd.ExecuteNonQuery();
                EnsureAgentProfileColumn(conn);
                _initialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize chat session SQLite schema");
                throw;
            }
        }
    }

    private static void ApplyPragmas(SqliteConnection conn)
    {
        using var p1 = conn.CreateCommand();
        p1.CommandText = "PRAGMA foreign_keys = ON;";
        p1.ExecuteNonQuery();
        using var p2 = conn.CreateCommand();
        p2.CommandText = "PRAGMA journal_mode = WAL;";
        p2.ExecuteNonQuery();
    }

    private static void EnsureAgentProfileColumn(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(chat_sessions);";
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var colName = r.GetString(1);
                if (string.Equals(colName, "agent_profile_id", StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE chat_sessions ADD COLUMN agent_profile_id TEXT NULL;";
        alter.ExecuteNonQuery();
    }

    public async Task SaveFromHistoryAsync(string sessionId, IReadOnlyList<ChatMessage> history, string? agentProfileId = null, CancellationToken ct = default)
    {
        if (!ChatSessionPersistenceHelper.IsValidSessionId(sessionId))
            return;

        EnsureInitialized();
        var lines = ChatSessionPersistenceHelper.ExtractTranscriptLines(history);
        var userCount = lines.Count(l => string.Equals(l.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (userCount == 0)
        {
            await TryDeleteAsync(sessionId, ct).ConfigureAwait(false);
            return;
        }

        var title = lines.FirstOrDefault(l => string.Equals(l.Role, "user", StringComparison.OrdinalIgnoreCase))?.Text ?? "";
        if (title.Length > 80)
            title = title[..80] + "…";

        var now = DateTime.UtcNow;
        var nowStr = FormatUtc(now);

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var tx = conn.BeginTransaction();
            await using (var delMsg = conn.CreateCommand())
            {
                delMsg.Transaction = tx;
                delMsg.CommandText = "DELETE FROM chat_session_messages WHERE session_id = $sid;";
                delMsg.Parameters.AddWithValue("$sid", sessionId);
                await delMsg.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var delSess = conn.CreateCommand())
            {
                delSess.Transaction = tx;
                delSess.CommandText = "DELETE FROM chat_sessions WHERE session_id = $sid;";
                delSess.Parameters.AddWithValue("$sid", sessionId);
                await delSess.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            object aidDb = string.IsNullOrWhiteSpace(agentProfileId) ? DBNull.Value : agentProfileId.Trim();
            await using (var insSess = conn.CreateCommand())
            {
                insSess.Transaction = tx;
                insSess.CommandText = """
                    INSERT INTO chat_sessions (session_id, updated_at_utc, title_preview, message_count, agent_profile_id)
                    VALUES ($sid, $upd, $title, $cnt, $aid);
                    """;
                insSess.Parameters.AddWithValue("$sid", sessionId);
                insSess.Parameters.AddWithValue("$upd", nowStr);
                insSess.Parameters.AddWithValue("$title", title.Trim());
                insSess.Parameters.AddWithValue("$cnt", lines.Count);
                insSess.Parameters.AddWithValue("$aid", aidDb);
                await insSess.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            for (var i = 0; i < lines.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var line = lines[i];
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO chat_session_messages (session_id, sort_order, role, text, created_at_utc)
                    VALUES ($sid, $ord, $role, $text, $created);
                    """;
                ins.Parameters.AddWithValue("$sid", sessionId);
                ins.Parameters.AddWithValue("$ord", i);
                ins.Parameters.AddWithValue("$role", line.Role);
                ins.Parameters.AddWithValue("$text", line.Text);
                ins.Parameters.AddWithValue("$created", FormatUtc(line.CreatedAtUtc));
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Save chat transcript failed for session {SessionId}", sessionId);
        }
    }

    public async Task<IReadOnlyList<ChatSessionMessageDto>?> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        if (!ChatSessionPersistenceHelper.IsValidSessionId(sessionId))
            return null;

        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT role, text, created_at_utc FROM chat_session_messages
            WHERE session_id = $sid ORDER BY sort_order ASC;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var list = new List<ChatSessionMessageDto>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var createdStr = r.GetString(2);
                if (!DateTime.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
                    created = DateTime.UtcNow;
                list.Add(new ChatSessionMessageDto
                {
                    Role = r.GetString(0),
                    Text = r.GetString(1),
                    CreatedAtUtc = created
                });
            }
        }

        return list.Count == 0 ? null : list;
    }

    public IReadOnlyList<ChatSessionMessageDto>? TryGetPersistedMessages(string sessionId)
    {
        if (!ChatSessionPersistenceHelper.IsValidSessionId(sessionId))
            return null;

        EnsureInitialized();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            ApplyPragmas(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT role, text, created_at_utc FROM chat_session_messages
                WHERE session_id = $sid ORDER BY sort_order ASC;
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            var list = new List<ChatSessionMessageDto>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var createdStr = r.GetString(2);
                if (!DateTime.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
                    created = DateTime.UtcNow;
                list.Add(new ChatSessionMessageDto
                {
                    Role = r.GetString(0),
                    Text = r.GetString(1),
                    CreatedAtUtc = created
                });
            }

            return list.Count == 0 ? null : list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read chat messages for session {SessionId}", sessionId);
            return null;
        }
    }

    public async Task<(IReadOnlyList<ChatSessionListItemDto> Items, bool HasMore)> ListAsync(int skip, int take, string? agentProfileId = null, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 50);
        var filterAid = string.IsNullOrWhiteSpace(agentProfileId) ? null : agentProfileId.Trim();

        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long total;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = filterAid == null
                ? "SELECT COUNT(*) FROM chat_sessions;"
                : "SELECT COUNT(*) FROM chat_sessions WHERE agent_profile_id = $aid;";
            if (filterAid != null)
                countCmd.Parameters.AddWithValue("$aid", filterAid);
            var o = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            total = o is long l ? l : Convert.ToInt64(o, CultureInfo.InvariantCulture);
        }

        var slice = new List<ChatSessionListItemDto>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = filterAid == null
                ? """
                  SELECT session_id, title_preview, updated_at_utc, message_count, agent_profile_id
                  FROM chat_sessions
                  ORDER BY updated_at_utc DESC
                  LIMIT $take OFFSET $skip;
                  """
                : """
                  SELECT session_id, title_preview, updated_at_utc, message_count, agent_profile_id
                  FROM chat_sessions
                  WHERE agent_profile_id = $aid
                  ORDER BY updated_at_utc DESC
                  LIMIT $take OFFSET $skip;
                  """;
            cmd.Parameters.AddWithValue("$take", take);
            cmd.Parameters.AddWithValue("$skip", skip);
            if (filterAid != null)
                cmd.Parameters.AddWithValue("$aid", filterAid);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var updStr = r.GetString(2);
                if (!DateTime.TryParse(updStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var upd))
                    upd = DateTime.UtcNow;
                slice.Add(new ChatSessionListItemDto
                {
                    SessionId = r.GetString(0),
                    TitlePreview = r.GetString(1),
                    UpdatedAtUtc = upd,
                    MessageCount = r.GetInt32(3),
                    AgentProfileId = r.IsDBNull(4) ? null : r.GetString(4)
                });
            }
        }

        var hasMore = skip + slice.Count < total;
        return (slice, hasMore);
    }

    public async Task<bool> TryDeleteAsync(string sessionId, CancellationToken ct = default)
    {
        if (!ChatSessionPersistenceHelper.IsValidSessionId(sessionId))
            return false;

        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chat_sessions WHERE session_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return n > 0;
    }

    private static string FormatUtc(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Local)
            dt = dt.ToUniversalTime();
        else if (dt.Kind == DateTimeKind.Unspecified)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return dt.ToString("o", CultureInfo.InvariantCulture);
    }
}
