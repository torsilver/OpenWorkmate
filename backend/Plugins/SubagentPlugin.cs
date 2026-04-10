using System.ComponentModel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>同会话内子代理：run_subtask 在隔离上下文中执行子任务，仅将最终结果返回主 Agent，避免大量 tool 输出占用主上下文。</summary>
[CopilotPluginId("Subagent")]
public sealed class SubagentPlugin
{
    private readonly ChatService _chatService;

    public SubagentPlugin(ChatService chatService)
    {
        _chatService = chatService;
    }

    /// <summary>将复杂或多步子任务交给子代理执行，子代理在独立上下文中运行并只返回最终总结，主对话不会塞入中间过程。</summary>
    [ToolFunction("run_subtask")]
    [Description("当需要执行一段相对独立、多步骤或会产出大量中间结果的任务（如深度检索、多轮读文件、批量操作）时，可调用此工具将任务交给子代理。子代理在隔离上下文中完成并只返回最终总结，主对话仅收到该总结。taskDescription 需写清要完成的具体目标。")]
    public async Task<string> RunSubtaskAsync(
        [Description("要子代理完成的任务描述，清晰具体")] string taskDescription,
        [Description("可选约束或补充说明，如格式要求、范围限制")] string? constraints = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return await _chatService.RunSubtaskAsync(sessionId ?? "", taskDescription, constraints, cancellationToken).ConfigureAwait(false);
    }
}
