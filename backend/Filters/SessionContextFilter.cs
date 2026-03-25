using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Filters;

/// <summary>
/// Injects the current WebSocket chat session ID (from <see cref="SessionContext"/>) into
/// <c>sessionId</c> arguments for plugins that target the live browser/office RPC session.
/// Must <b>not</b> run for <see cref="OfficeCopilot.Server.Plugins.MeetingTranscriptPlugin"/>：其 <c>sessionId</c> 为会议落盘 id（如 <c>meeting_xxx</c>），与聊天会话 id 无关。
/// </summary>
public sealed class SessionContextFilter : IFunctionInvocationFilter
{
    private readonly ILogger<SessionContextFilter> _logger;

    public SessionContextFilter(ILogger<SessionContextFilter> logger) => _logger = logger;

    private static bool IsMeetingTranscriptFunction(string? functionName) =>
        string.Equals(functionName, "meeting_transcript_read", StringComparison.OrdinalIgnoreCase)
        || string.Equals(functionName, "meeting_transcript_meta", StringComparison.OrdinalIgnoreCase);

    public Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        if (IsMeetingTranscriptFunction(context.Function.Name))
            return next(context);

        var sessionId = SessionContext.GetSessionId();
        if (!string.IsNullOrEmpty(sessionId))
        {
            context.Arguments["sessionId"] = sessionId;
            _logger.LogDebug("[Filter] Injected sessionId into {Function}", context.Function.Name);
        }
        else
        {
            _logger.LogWarning("[Filter] SessionContext.GetSessionId() is null for function {Function}", context.Function.Name);
        }

        return next(context);
    }
}
