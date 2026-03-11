using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Mcp;

namespace OfficeCopilot.Server;

/// <summary>
/// Routes incoming messages and produces responses.
/// Phase 1: simple echo. Phase 2+: forwards to Semantic Kernel.
/// </summary>
public static class MessageRouter
{
    public static string Process(string sessionId, string raw)
    {
        WsMessage? incoming;
        try
        {
            incoming = JsonSerializer.Deserialize<WsMessage>(raw);
        }
        catch
        {
            incoming = new WsMessage { Type = "text", Content = raw };
        }

        if (incoming is null || string.IsNullOrEmpty(incoming.Content))
            return Serialize(new WsMessage
            {
                Type = "error",
                Content = "Empty message."
            });

        return incoming.Type switch
        {
            "ping" => Serialize(new WsMessage { Type = "pong", Content = "pong" }),
            _ => Serialize(new WsMessage
            {
                Type = "echo",
                Content = incoming.Content,
                SessionId = sessionId,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            })
        };
    }

    private static string Serialize(WsMessage msg) =>
        JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
}

public class WsMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; set; }

    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Timestamp { get; set; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebPageContext? Context { get; set; }

    // RPC fields
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Error { get; set; }

    /// <summary>HITL confirm_response: true = allow, false = deny.</summary>
    [JsonPropertyName("allowed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Allowed { get; set; }

    // 工具调用状态（tool_invocation_start / tool_invocation_end）
    [JsonPropertyName("plugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Plugin { get; set; }

    [JsonPropertyName("function")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Function { get; set; }

    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Success { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>User-attached images (base64 data + mime type) for multimodal chat.</summary>
    [JsonPropertyName("attachments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AttachmentDto>? Attachments { get; set; }
}

public class AttachmentDto
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "image/png";

    [JsonPropertyName("data")]
    public string Data { get; set; } = ""; // base64
}

public class WebPageContext
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>内置插件信息，供设置页展示「自带的 MCP」。</summary>
public class BuiltInPluginInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsMessage))]
[JsonSerializable(typeof(WebPageContext))]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AiConfig))]
[JsonSerializable(typeof(SkillDefinition))]
[JsonSerializable(typeof(List<SkillDefinition>))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(List<McpServerConfig>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(McpCallToolResult))]
[JsonSerializable(typeof(BuiltInPluginInfo))]
[JsonSerializable(typeof(List<BuiltInPluginInfo>))]
[JsonSerializable(typeof(AttachmentDto))]
[JsonSerializable(typeof(List<AttachmentDto>))]
internal partial class JsonCtx : JsonSerializerContext;
