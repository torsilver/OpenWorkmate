using System.Text.Json;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 可选 JSONL 会话审计（<see cref="ContextWindowConfig.SessionAuditEnabled"/>）；内容截断、不落 base64。
/// </summary>
public static class SessionAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void TryAppend(ContextWindowConfig? ctx, string sessionId, string eventType, object payload)
    {
        if (ctx is not { SessionAuditEnabled: true })
            return;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        try
        {
            var dir = ResolveDirectory(ctx);
            Directory.CreateDirectory(dir);
            var safe = string.Join("_", sessionId.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrEmpty(safe)) safe = "session";
            var path = Path.Combine(dir, safe + ".jsonl");
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                sessionId,
                eventType,
                payload
            }, JsonOptions);
            lock (typeof(SessionAuditLog))
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            /* best-effort */
        }
    }

    public static string SanitizeForAudit(string? text, int maxChars = 2000)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= maxChars ? t : t[..maxChars] + "…";
    }

    private static string ResolveDirectory(ContextWindowConfig ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.SessionAuditDirectory))
            return ctx.SessionAuditDirectory.Trim();
        if (!string.IsNullOrWhiteSpace(ctx.ConversationHistoryDirectory))
        {
            var full = Path.GetFullPath(ctx.ConversationHistoryDirectory);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                return Path.Combine(parent, "SessionAudit");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfficeCopilot", "SessionAudit");
    }
}
