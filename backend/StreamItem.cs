namespace OfficeCopilot.Server;

/// <summary>
/// 流式片段类型：正文、百炼 <c>reasoning_content</c>、工具参数增量、或警告。
/// <see cref="Reasoning"/> 仅用于前端时间线展示，不得参与重试/摘要/权限等业务分支（仅允许诊断日志）。
/// </summary>
public enum StreamSegmentKind
{
    Normal,
    Reasoning,
    ToolCallDelta
}

/// <summary>
/// 聊天流中的一项：普通文本块、推理文本块（百炼等）、工具调用参数流式增量，或需展示给用户的警告（如记忆/知识库检索失败）。
/// <see cref="StreamSegmentKind.Reasoning"/> 的正文不得写入会话历史缓冲，也不得作为策略判断依据。
/// </summary>
public record StreamItem(
    bool IsWarning,
    string Content,
    StreamSegmentKind Kind = StreamSegmentKind.Normal,
    ToolCallStreamDelta? ToolDelta = null);
