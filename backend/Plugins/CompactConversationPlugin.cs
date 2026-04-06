using System.ComponentModel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>上下文管理：提供 compact_conversation 工具，供模型在换任务或已总结完时主动压缩对话以释放上下文。</summary>
public sealed class CompactConversationPlugin
{
    private readonly ChatService _chatService;

    public CompactConversationPlugin(ChatService chatService)
    {
        _chatService = chatService;
    }

    /// <summary>主动压缩当前对话：将最旧若干轮合并为一段摘要，释放上下文窗口。适合在开始全新任务或已给出最终结论后调用。</summary>
    [ToolFunction("compact_conversation")]
    [Description("当对话较长且你要开始全新任务、或已给出最终结论不再需要此前详细内容时，可调用此工具压缩对话，将较早的轮次合并为摘要以释放上下文。")]
    public async Task<string> CompactConversationAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = SessionContext.GetSessionId();
        return await _chatService.CompactConversationAsync(sessionId ?? "", cancellationToken).ConfigureAwait(false);
    }
}
