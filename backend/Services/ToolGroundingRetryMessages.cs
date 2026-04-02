namespace OfficeCopilot.Server.Services;

public static class ToolGroundingRetryMessages
{
    /// <summary>作为临时 user 消息注入重试轮 API 请求，不写入持久会话历史。</summary>
    public const string NudgeUserMessage =
        "[系统提示] 你上一轮在可用工具的情况下未调用任何工具，却描述了操作结果。用户请求仍需通过合适的 function call 在本机完成；你必须在本轮先调用工具，只有在收到工具返回后，才能向用户说明成功或失败。禁止在未调用工具时声称已完成文件或单元格变更。";

    /// <summary>读类工具接地重试：用户已点名读 Kernel 时禁止用对话历史代替磁盘文件事实。</summary>
    public const string ReadNudgeUserMessage =
        "[系统提示] 用户消息中已点名只读类文档工具，但你上一轮未调用任何工具。对话历史或旧摘要不能代表当前磁盘上的文件内容；你必须在本轮先调用合适的读类 function call（如路径与参数与用户意图一致），仅在收到工具返回后再向用户整理结果。禁止仅凭记忆复述「已读取」或推断文件现状。";
}
