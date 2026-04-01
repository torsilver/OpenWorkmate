namespace OfficeCopilot.Server.Services;

public static class ToolGroundingRetryMessages
{
    /// <summary>作为临时 user 消息注入重试轮 API 请求，不写入持久会话历史。</summary>
    public const string NudgeUserMessage =
        "[系统提示] 你上一轮在可用工具的情况下未调用任何工具，却描述了操作结果。用户请求仍需通过合适的 function call 在本机完成；你必须在本轮先调用工具，只有在收到工具返回后，才能向用户说明成功或失败。禁止在未调用工具时声称已完成文件或单元格变更。";
}
