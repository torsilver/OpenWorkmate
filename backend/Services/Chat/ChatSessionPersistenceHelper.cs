using System.Text;
using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.Chat;

/// <summary>会话 ID 校验与从 <see cref="ChatMessage"/> 列表抽取可持久化文本行（不含 system）。</summary>
public static class ChatSessionPersistenceHelper
{
    public static bool IsValidSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (sessionId.Length > 64) return false;
        foreach (var c in sessionId)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }

    public static List<ChatSessionMessageDto> ExtractTranscriptLines(IReadOnlyList<ChatMessage> history)
    {
        var list = new List<ChatSessionMessageDto>();
        foreach (var msg in history)
        {
            if (msg.Role == ChatRole.System)
                continue;

            var text = GetPlainText(msg);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            string role;
            if (msg.Role == ChatRole.User)
                role = "user";
            else if (msg.Role == ChatRole.Assistant)
                role = "assistant";
            else
                role = "assistant";
            list.Add(new ChatSessionMessageDto
            {
                Role = role,
                Text = text.Trim(),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        return list;
    }

    private static string GetPlainText(ChatMessage msg)
    {
        var t = msg.Text;
        if (!string.IsNullOrEmpty(t))
            return t;
        if (msg.Contents is not { Count: > 0 })
            return "";
        var sb = new StringBuilder();
        foreach (var item in msg.Contents)
        {
            if (item is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                sb.Append(tc.Text);
        }
        return sb.ToString();
    }
}
