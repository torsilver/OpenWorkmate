using System.Text.Json.Serialization;

namespace OpenWorkmate.Server.Services.Chat;

/// <summary>GET /api/chat-sessions 单条摘要。</summary>
public sealed class ChatSessionListItemDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("titlePreview")]
    public string TitlePreview { get; set; } = "";

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    /// <summary>握手时的 Agent 配置 Id；旧会话可能为空。</summary>
    [JsonPropertyName("agentProfileId")]
    public string? AgentProfileId { get; set; }
}

/// <summary>GET /api/chat-sessions/{id}/messages 单条消息。</summary>
public sealed class ChatSessionMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class ChatSessionListResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("items")]
    public List<ChatSessionListItemDto> Items { get; set; } = new();

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

public sealed class ChatSessionMessagesResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("messages")]
    public List<ChatSessionMessageDto> Messages { get; set; } = new();
}
