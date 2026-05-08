using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenWorkmate.Server.Services.Maf;

/// <summary>
/// MAF Agent Run Middleware 工厂。通过 <c>agent.AsBuilder().Use(shared).Build()</c> 注册到 Agent 管道。
/// </summary>
public static class AgentRunMiddleware
{
    /// <summary>
    /// 上下文长度重试中间件：捕获 context_length 错误 → 裁剪历史 → 设置重试标记。
    /// 适用于主会话 Agent（通过 shared Use 注册，同时覆盖非流式和流式路径）。
    /// </summary>
    public static Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task>, CancellationToken, Task>
        CreateContextLengthRetry(
            SessionState state, ContextWindowConfig ctxConfig, StreamPassOutcome outcome, bool allowContextRetry)
    {
        return async (messages, session, opts, next, ct) =>
        {
            try
            {
                await next(messages, session, opts, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (allowContextRetry && ContextLengthRetryHelper.IsContextLengthError(ex))
            {
                ContextLengthRetryHelper.TrimHistoryForRetry(state.History, ctxConfig.ContextLengthRetryMaxTurns, ctxConfig);
                outcome.ContextLengthRetryRequested = true;
            }
        };
    }
}
