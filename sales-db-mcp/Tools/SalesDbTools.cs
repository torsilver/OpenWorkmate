using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SalesDbMcp;

namespace SalesDbMcp.Tools;

/// <summary>
/// MCP tools for the sales SQL Server database (read-only).
/// Connection string is configured via SalesDb:ConnectionString or SALES_DB_CONNECTION_STRING.
/// </summary>
internal class SalesDbTools
{
    private readonly SalesDbOptions _options;

    public SalesDbTools(Microsoft.Extensions.Options.IOptions<SalesDbOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    private string GetConnectionString()
    {
        var cs = _options.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "Sales DB connection string is not configured. Set SalesDb:ConnectionString in appsettings.json or SALES_DB_CONNECTION_STRING environment variable.");
        return cs;
    }

    [McpServerTool]
    [Description("Check connectivity to the sales database. Returns success or error message.")]
    public async Task<string> SalesDbHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqlConnection(GetConnectionString());
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return "OK: Sales database connection succeeded.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Execute a read-only SELECT query against the sales database. Only SELECT statements are allowed. Returns result rows as JSON-friendly text.")]
    public async Task<string> SalesDbQueryAsync(
        [Description("SQL SELECT query to execute")] string query,
        [Description("Query timeout in seconds")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var trimmed = query.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only SELECT queries are allowed. Refused non-SELECT statement.");

        await using var conn = new SqlConnection(GetConnectionString());
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        cmd.CommandTimeout = Math.Clamp(timeoutSeconds, 1, 300);

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rowIndex = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rowIndex == 0)
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    sb.Append(reader.GetName(i)).Append(i < reader.FieldCount - 1 ? "\t" : "\n");
            }
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var val = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
                sb.Append(val).Append(i < reader.FieldCount - 1 ? "\t" : "\n");
            }
            rowIndex++;
        }

        if (rowIndex == 0)
            return "(no rows)";
        return sb.ToString();
    }

    [McpServerTool]
    [Description("List table and view names in the sales database. Optionally filter by schema (e.g. dbo).")]
    public async Task<string> SalesDbListTablesAsync(
        [Description("Schema name to filter (e.g. dbo). Leave empty for all schemas.")] string? schema = null,
        CancellationToken cancellationToken = default)
    {
        const string sqlAll = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        const string sqlFiltered = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW') AND TABLE_SCHEMA = @schema
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;

        await using var conn = new SqlConnection(GetConnectionString());
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(schema))
        {
            cmd.CommandText = sqlAll;
        }
        else
        {
            cmd.CommandText = sqlFiltered;
            cmd.Parameters.AddWithValue("@schema", schema.Trim());
        }

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sb.Append(reader.GetString(0)).Append('.').Append(reader.GetString(1)).Append(" [").Append(reader.GetString(2)).Append("]\n");
        return sb.Length == 0 ? "(no tables or views)" : sb.ToString();
    }

    [McpServerTool]
    [Description("Get column names, types, and key info for a table or view in the sales database. Use TABLE_SCHEMA.TABLE_NAME or TABLE_NAME.")]
    public async Task<string> SalesDbGetSchemaAsync(
        [Description("Table or view name, e.g. dbo.Orders or Orders")] string tableName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("tableName is required.", nameof(tableName));

        var parts = tableName.Trim().Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var name = parts.Length == 2 ? parts[1] : parts[0];

        const string sql = """
            SELECT c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH,
                   CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA AND tc.TABLE_NAME = ku.TABLE_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON c.TABLE_SCHEMA = pk.TABLE_SCHEMA AND c.TABLE_NAME = pk.TABLE_NAME AND c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @name
            ORDER BY c.ORDINAL_POSITION
            """;

        await using var conn = new SqlConnection(GetConnectionString());
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", name);

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var col = reader.GetString(0);
            var type = reader.GetString(1);
            var nullable = reader.GetString(2);
            var maxLen = reader.IsDBNull(3) ? "" : reader.GetInt32(3).ToString();
            var pk = reader.GetInt32(4) == 1 ? " PK" : "";
            sb.Append(col).Append(" ").Append(type);
            if (!string.IsNullOrEmpty(maxLen) && (type.Contains("char", StringComparison.OrdinalIgnoreCase) || type.Contains("binary", StringComparison.OrdinalIgnoreCase)))
                sb.Append('(').Append(maxLen).Append(')');
            sb.Append(nullable == "YES" ? " NULL" : " NOT NULL").Append(pk).Append('\n');
        }
        return sb.Length == 0 ? $"(no columns found for {tableName})" : sb.ToString();
    }
}
