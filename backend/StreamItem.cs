namespace OfficeCopilot.Server;

/// <summary>流式片段类型：正文、百炼 <c>reasoning_content</c>、或警告。</summary>
public enum StreamSegmentKind
{
    Normal,
    Reasoning
}

/// <summary>
/// 聊天流中的一项：普通文本块、推理文本块（百炼等），或需展示给用户的警告（如记忆/知识库检索失败）。
/// </summary>
public record StreamItem(bool IsWarning, string Content, StreamSegmentKind Kind = StreamSegmentKind.Normal);
