namespace OfficeCopilot.Server;

/// <summary>
/// 流式片段类型：正文、百炼等 <c>reasoning_content</c>、工具参数增量、OpenAI 兼容流元数据（用量/结束原因/角色/响应 id）、或警告。
/// <see cref="Reasoning"/> 与 <see cref="StreamUsage"/> / <see cref="StreamFinish"/> / <see cref="StreamRole"/> / <see cref="StreamMeta"/> 仅用于前端时间线展示，不得参与重试/摘要/权限等业务分支（仅允许诊断日志）。
/// </summary>
public enum StreamSegmentKind
{
    Normal,
    Reasoning,
    ToolCallDelta,
    /// <summary>顶层 <c>usage</c> JSON 对象原文（<see cref="StreamItem.Content"/>）。</summary>
    StreamUsage,
    /// <summary><c>finish_reason</c> 字符串（<see cref="StreamItem.Content"/>）。</summary>
    StreamFinish,
    /// <summary>首个 <c>delta.role</c>（<see cref="StreamItem.Content"/>）。</summary>
    StreamRole,
    /// <summary>响应级元数据 JSON（id/model/created 等，<see cref="StreamItem.Content"/>）。</summary>
    StreamMeta
}

/// <summary>
/// 聊天流中的一项：普通文本块、推理文本块、工具调用参数流式增量、流式元数据块，或需展示给用户的警告（如记忆/知识库检索失败）。
/// <see cref="StreamSegmentKind.Reasoning"/> 及元数据类的正文不得写入会话历史缓冲，也不得作为策略判断依据。
/// </summary>
public record StreamItem(
    bool IsWarning,
    string Content,
    StreamSegmentKind Kind = StreamSegmentKind.Normal,
    ToolCallStreamDelta? ToolDelta = null);
