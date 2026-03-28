using System.Collections.Concurrent;

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>
/// 与 <see cref="DashScopeOpenAiCompatHandler"/> 配合：在百炼 SSE 旁路解析出的 <c>reasoning_content</c> 片段入队，
/// 由 <see cref="OfficeCopilot.Server.ChatService"/> 在流式轮次中与正文块交错 drain。
/// 使用 AsyncLocal + 栈以支持同一会话内嵌套 HTTP 调用（极少见）并隔离并行 Task。
/// </summary>
internal static class DashScopeReasoningContext
{
    private static readonly AsyncLocal<Stack<ConcurrentQueue<string>>?> Stacks = new();

    internal static void PushFrame()
    {
        var stack = Stacks.Value;
        if (stack == null)
        {
            stack = new Stack<ConcurrentQueue<string>>();
            Stacks.Value = stack;
        }

        stack.Push(new ConcurrentQueue<string>());
    }

    internal static void PopFrame()
    {
        var stack = Stacks.Value;
        if (stack is { Count: > 0 })
            stack.Pop();
        if (stack is { Count: 0 })
            Stacks.Value = null;
    }

    internal static void EnqueueReasoning(string? fragment)
    {
        if (string.IsNullOrEmpty(fragment))
            return;
        var stack = Stacks.Value;
        if (stack is not { Count: > 0 })
            return;
        stack.Peek().Enqueue(fragment);
    }

    /// <summary>取出当前帧内已解析的推理片段（FIFO），不结束帧。</summary>
    internal static IEnumerable<string> DrainCurrentFrame()
    {
        var stack = Stacks.Value;
        if (stack is not { Count: > 0 })
            yield break;
        var q = stack.Peek();
        while (q.TryDequeue(out var s))
        {
            if (!string.IsNullOrEmpty(s))
                yield return s;
        }
    }
}
