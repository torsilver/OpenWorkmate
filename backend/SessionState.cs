using Microsoft.SemanticKernel.ChatCompletion;

namespace OfficeCopilot.Server;

/// <summary>单会话的运行时状态（历史等）。供 <see cref="ChatService"/> 与本轮编排上下文共享。</summary>
public sealed class SessionState
{
    public ChatHistory History { get; }
    public DateTime LastActivity { get; private set; }

    public SessionState(string systemPrompt)
    {
        History = new ChatHistory(systemPrompt);
        Touch();
    }

    public void Touch() => LastActivity = DateTime.UtcNow;
}
