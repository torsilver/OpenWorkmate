namespace OfficeCopilot.Server;

/// <summary>
/// 聊天流中的一项：普通文本块或需展示给用户的警告（如记忆/知识库检索失败）。
/// </summary>
public record StreamItem(bool IsWarning, string Content);
