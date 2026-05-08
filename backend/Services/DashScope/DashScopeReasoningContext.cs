using System.Collections.Concurrent;

namespace OpenWorkmate.Server.Services.DashScope;

/// <summary>
/// 与 <see cref="DashScopeOpenAiCompatHandler"/> 配合：在百炼 SSE 旁路解析出的 <c>reasoning_content</c> 片段入队，
/// 由 <see cref="OpenWorkmate.Server.ChatService"/> 在流式轮次中与正文块交错 drain。
/// <b>旁路推理仅用于下发 <c>reasoning_chunk</c>，不写入 <c>fullResponse</c> / 会话历史，也不得参与工具接地等策略分支。</b>
/// 使用 AsyncLocal + 栈以支持嵌套 HTTP 调用；<b>SSE 读可能在未传播 ExecutionContext 的线程上回调</b>，
/// 故 Handler 必须用 <see cref="PushFrame"/> 返回的队列引用闭包入队，不得依赖 <see cref="TryEnqueueReasoning"/> 在该回调路径上工作。
/// </summary>
internal static class DashScopeReasoningContext
{
    private static readonly AsyncLocal<Stack<ConcurrentQueue<string>>?> Stacks = new();

    /// <returns>本帧队列；SSE tap 应对该实例 <c>Enqueue</c>，保证与 HttpClient 读流线程无关。</returns>
    internal static ConcurrentQueue<string> PushFrame()
    {
        var stack = Stacks.Value;
        if (stack == null)
        {
            stack = new Stack<ConcurrentQueue<string>>();
            Stacks.Value = stack;
        }

        var q = new ConcurrentQueue<string>();
        stack.Push(q);
        return q;
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
        TryEnqueueReasoning(fragment);
    }

    /// <returns>false 表示当前 AsyncLocal 无帧，推理片段被丢弃（用于诊断）。</returns>
    internal static bool TryEnqueueReasoning(string? fragment)
    {
        if (string.IsNullOrEmpty(fragment))
            return true;
        var stack = Stacks.Value;
        if (stack is not { Count: > 0 })
            return false;
        stack.Peek().Enqueue(fragment);
        return true;
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
