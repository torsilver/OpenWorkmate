using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Filters;

/// <summary>
/// Injects the current session ID (from SessionContext) into kernel function arguments
/// so that plugins like BrowserPlugin can access it when invoked by the chat completion.
/// </summary>
public sealed class SessionContextFilter : IFunctionInvocationFilter
{
    private readonly ILogger<SessionContextFilter> _logger;

    public SessionContextFilter(ILogger<SessionContextFilter> logger) => _logger = logger;

    public Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
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
