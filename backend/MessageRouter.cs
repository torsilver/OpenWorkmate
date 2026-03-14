using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Plan;
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

    /// <summary>可选：绑定知识库 ID，对话时将检索该知识库并注入上下文（RAG）。</summary>
    [JsonPropertyName("knowledgeBaseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KnowledgeBaseId { get; set; }

    /// <summary>对话模式：plan = 仅生成计划，agent = 执行（可带计划）。</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    /// <summary>当前绑定的计划 ID，Agent 模式下注入计划供执行。</summary>
    [JsonPropertyName("planId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanId { get; set; }

    /// <summary>执行计划时的当前步骤索引（从 1 开始）；不传或 0 表示第 1 步。仅注入该步内容以节省上下文。</summary>
    [JsonPropertyName("planCurrentStepIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanCurrentStepIndex { get; set; }

    /// <summary>plan_created 时：计划标题。</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>plan_created 时：计划文件路径或相对路径。</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>plan_created 时：创建该计划的端（chrome | office-word | office-excel | wps）。</summary>
    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    /// <summary>plan_created 时：是否需用户确认后再执行（由后台规则计算）。</summary>
    [JsonPropertyName("requiresUserConfirmation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresUserConfirmation { get; set; }
}

public class AttachmentDto
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "image/png";

    [JsonPropertyName("data")]
    public string Data { get; set; } = ""; // base64
}

/// <summary>RAG 摄入请求。</summary>
public class RagIngestRequest
{
    [JsonPropertyName("knowledgeBaseId")]
    public string KnowledgeBaseId { get; set; } = "";
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
    [JsonPropertyName("maxChunkChars")]
    public int MaxChunkChars { get; set; } = 800;
    [JsonPropertyName("overlapChars")]
    public int OverlapChars { get; set; } = 50;
}

/// <summary>记忆新增请求。</summary>
public class MemoryAddRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }
    /// <summary>为 true 时写入共享记忆（跨端可见）。</summary>
    [JsonPropertyName("scopeShared")]
    public bool ScopeShared { get; set; }
}

/// <summary>记忆更新请求。</summary>
public class MemoryUpdateRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }
    /// <summary>为 true 时改为共享记忆，为 false 时改为仅本会话（原为共享时改为 sessionId=null）。不传则保持原 scope。</summary>
    [JsonPropertyName("scopeShared")]
    public bool? ScopeShared { get; set; }
}

/// <summary>内置插件信息，供设置页展示「自带的 MCP」。</summary>
public class BuiltInPluginInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>PUT /api/plans/{id} 请求体。</summary>
public class PlanUpdateRequest
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

/// <summary>POST /api/scheduled-tasks 请求体。</summary>
public class ScheduledTaskCreateRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
    [JsonPropertyName("scheduleType")]
    public string ScheduleType { get; set; } = "cron";
    [JsonPropertyName("cronExpression")]
    public string? CronExpression { get; set; }
    [JsonPropertyName("intervalMinutes")]
    public int? IntervalMinutes { get; set; }
    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }
    [JsonPropertyName("endAt")]
    public DateTimeOffset? EndAt { get; set; }
    [JsonPropertyName("maxRuns")]
    public int? MaxRuns { get; set; }
    [JsonPropertyName("deleteAfterRun")]
    public bool DeleteAfterRun { get; set; }
}

/// <summary>PUT /api/scheduled-tasks/{id} 请求体。</summary>
public class ScheduledTaskUpdateRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("content")]
    public string? Content { get; set; }
    [JsonPropertyName("scheduleType")]
    public string? ScheduleType { get; set; }
    [JsonPropertyName("cronExpression")]
    public string? CronExpression { get; set; }
    [JsonPropertyName("intervalMinutes")]
    public int? IntervalMinutes { get; set; }
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }
    [JsonPropertyName("endAt")]
    public DateTimeOffset? EndAt { get; set; }
    [JsonPropertyName("maxRuns")]
    public int? MaxRuns { get; set; }
    [JsonPropertyName("deleteAfterRun")]
    public bool? DeleteAfterRun { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsMessage))]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AiConfig))]
[JsonSerializable(typeof(SessionConfig))]
[JsonSerializable(typeof(ContextWindowConfig))]
[JsonSerializable(typeof(PlanConfirmationConfig))]
[JsonSerializable(typeof(ContextOptimizationPreset))]
[JsonSerializable(typeof(List<ContextOptimizationPreset>))]
[JsonSerializable(typeof(AiModelEntry))]
[JsonSerializable(typeof(List<AiModelEntry>))]
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
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(TestAiRequest))]
[JsonSerializable(typeof(RagIngestRequest))]
[JsonSerializable(typeof(MemoryAddRequest))]
[JsonSerializable(typeof(MemoryUpdateRequest))]
[JsonSerializable(typeof(OfficeCopilot.Server.Services.Memory.MemoryRecord))]
[JsonSerializable(typeof(PlanMeta))]
[JsonSerializable(typeof(List<PlanMeta>))]
[JsonSerializable(typeof(PlanUpdateRequest))]
[JsonSerializable(typeof(OfficeCopilot.Server.Services.ScheduledTask.ScheduledTaskMeta))]
[JsonSerializable(typeof(List<OfficeCopilot.Server.Services.ScheduledTask.ScheduledTaskMeta>))]
[JsonSerializable(typeof(ScheduledTaskCreateRequest))]
[JsonSerializable(typeof(ScheduledTaskUpdateRequest))]
internal partial class JsonCtx : JsonSerializerContext;
