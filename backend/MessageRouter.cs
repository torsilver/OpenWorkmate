using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Plan;
using OfficeCopilot.Server.Mcp;

namespace OfficeCopilot.Server;

/// <summary>WebSocket 消息。type=<c>agent_status</c> 为准备阶段文案；<c>agent_trace</c> 为内部过程详情；<c>agent_phase</c> 配合 <see cref="Phase"/>（intent/digest）；<c>reasoning_chunk</c> 为模型推理增量（与 <c>stream_chunk</c> 并列），仅供 UI 按序展示，不得在后端或扩展内参与业务判断。</summary>
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

    /// <summary>HITL confirm_response: 是否同时加入当前端白名单并执行。</summary>
    [JsonPropertyName("addToAllowList")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AddToAllowList { get; set; }

    /// <summary>ask_options_response: 单选/多轮候选项最终选择结果（stepId -> optionId）。</summary>
    [JsonPropertyName("selections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Selections { get; set; }

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

    /// <summary>set_context 时：当前浏览页标题；仅写入会话的页面上下文字段，不覆盖 Agent 展示名。</summary>
    [JsonPropertyName("pageTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PageTitle { get; set; }

    /// <summary>tool_invocation_start/end 时：Plan.execute_plan_step 的步骤索引（从 1 开始），供前端 checklist 更新。</summary>
    [JsonPropertyName("planStepIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanStepIndex { get; set; }

    /// <summary>plan_created 时：计划标题。</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>plan_created 时：计划文件路径或相对路径。</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>plan_created 时：创建该计划的端（chrome | office-word | office-excel | office-powerpoint | wps）。</summary>
    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    /// <summary>subtask_start 时：子任务描述。</summary>
    [JsonPropertyName("taskDescription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskDescription { get; set; }

    /// <summary>subtask_start 时：可选约束说明。</summary>
    [JsonPropertyName("constraints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Constraints { get; set; }

    /// <summary>tool_invocation_start/end 与 tool_call_delta：是否属于子代理内，供前端归入子代理块。</summary>
    [JsonPropertyName("isSubtask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSubtask { get; set; }

    /// <summary>agent_phase 时：阶段名，如 intent、digest。</summary>
    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; set; }

    /// <summary>agent_trace 时：memory | knowledgeBase | toolSelection | context（摘要/截断等上下文治理）。</summary>
    [JsonPropertyName("traceCategory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceCategory { get; set; }

    /// <summary>agent_trace 时：一行摘要。</summary>
    [JsonPropertyName("traceTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceTitle { get; set; }

    /// <summary>agent_trace 时：多行详情（服务端截断）。</summary>
    [JsonPropertyName("traceDetail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceDetail { get; set; }

    /// <summary>ui_theme_changed 时：对话界面预设主题 id（与 AppConfig.UiThemeId 一致）。</summary>
    [JsonPropertyName("uiThemeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UiThemeId { get; set; }

    /// <summary>tool_call_delta：OpenAI/SK 流式工具调用的 call_id（无则服务端用占位 key）。</summary>
    [JsonPropertyName("toolCallId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    /// <summary>tool_call_delta：工具名片段（可能随流式逐步完整）。</summary>
    [JsonPropertyName("toolName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; set; }

    /// <summary>tool_call_delta：arguments JSON 的增量片段。</summary>
    [JsonPropertyName("argumentsDelta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArgumentsDelta { get; set; }
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
    [JsonPropertyName("intervalSeconds")]
    public int? IntervalSeconds { get; set; }
    [JsonPropertyName("runAt")]
    public DateTimeOffset? RunAt { get; set; }
    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }
    [JsonPropertyName("endAt")]
    public DateTimeOffset? EndAt { get; set; }
    [JsonPropertyName("maxRuns")]
    public int? MaxRuns { get; set; }
    [JsonPropertyName("deleteAfterRun")]
    public bool DeleteAfterRun { get; set; }
}

/// <summary>POST /api/meeting-transcript/segment 请求体（Chrome 会议监听实录落盘）。</summary>
public class MeetingTranscriptSegmentRequest
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }
    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }
    [JsonPropertyName("text")]
    public string? Text { get; set; }
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
    [JsonPropertyName("intervalSeconds")]
    public int? IntervalSeconds { get; set; }
    [JsonPropertyName("runAt")]
    public DateTimeOffset? RunAt { get; set; }
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
[JsonSerializable(typeof(AgentProfileEntry))]
[JsonSerializable(typeof(List<AgentProfileEntry>))]
[JsonSerializable(typeof(ToolPermissionRule))]
[JsonSerializable(typeof(List<ToolPermissionRule>))]
[JsonSerializable(typeof(SemanticKernelFeaturesConfig))]
[JsonSerializable(typeof(AiConfig))]
[JsonSerializable(typeof(SessionConfig))]
[JsonSerializable(typeof(ContextWindowConfig))]
[JsonSerializable(typeof(ContextOptimizationPreset))]
[JsonSerializable(typeof(List<ContextOptimizationPreset>))]
[JsonSerializable(typeof(AiModelEntry))]
[JsonSerializable(typeof(List<AiModelEntry>))]
[JsonSerializable(typeof(EmbeddingModelEntry))]
[JsonSerializable(typeof(List<EmbeddingModelEntry>))]
[JsonSerializable(typeof(RealtimeAsrConfig))]
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
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(TestAiRequest))]
[JsonSerializable(typeof(TestEmbeddingRequest))]
[JsonSerializable(typeof(OcrModelEntry))]
[JsonSerializable(typeof(List<OcrModelEntry>))]
[JsonSerializable(typeof(TestRealtimeAsrRequest))]
[JsonSerializable(typeof(TestOcrRequest))]
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
[JsonSerializable(typeof(MeetingTranscriptSegmentRequest))]
[JsonSerializable(typeof(AgentDebugStatsResponse))]
[JsonSerializable(typeof(ToolSelectionDebugStatsDto))]
[JsonSerializable(typeof(ToolInvocationDebugStatDto))]
[JsonSerializable(typeof(List<ToolInvocationDebugStatDto>))]
[JsonSerializable(typeof(DebugStatsResetResponse))]
[JsonSerializable(typeof(OfficeCopilot.Server.Services.Chat.ChatSessionListItemDto))]
[JsonSerializable(typeof(List<OfficeCopilot.Server.Services.Chat.ChatSessionListItemDto>))]
[JsonSerializable(typeof(OfficeCopilot.Server.Services.Chat.ChatSessionMessageDto))]
[JsonSerializable(typeof(List<OfficeCopilot.Server.Services.Chat.ChatSessionMessageDto>))]
[JsonSerializable(typeof(OfficeCopilot.Server.Services.Chat.ChatSessionListResponse))]
[JsonSerializable(typeof(OfficeCopilot.Server.Services.Chat.ChatSessionMessagesResponse))]
internal partial class JsonCtx : JsonSerializerContext;
