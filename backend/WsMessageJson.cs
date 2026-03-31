using System.Text.Json;

namespace OfficeCopilot.Server;

/// <summary>WebSocket 消息的 JSON 序列化辅助，避免各处重复拼装。</summary>
public static class WsMessageJson
{
    /// <summary>序列化 <c>agent_status</c> 消息；空内容返回 null（调用方勿发送）。</summary>
    public static string? SerializeAgentStatus(string? content)
    {
        var t = (content ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Length > 200)
            t = t.Substring(0, 200);
        var msg = new WsMessage { Type = "agent_status", Content = t };
        return JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
    }
}
