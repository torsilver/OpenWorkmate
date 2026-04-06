using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>Shared services for the tool invocation pipeline (registered as singleton).</summary>
public sealed class ToolInvocationPipelineServices
{
    public required ConfigService ConfigService { get; init; }
    public required ISecurityPipeline SecurityPipeline { get; init; }
    public required IToolStatusNotifier ToolStatus { get; init; }
}

/// <summary>HITL / 白名单安全检查；由 <see cref="SecurityPipeline"/> 实现。</summary>
public interface ISecurityPipeline
{
    /// <returns>Non-null string if the call should be blocked (the string is returned to the model as the tool result).</returns>
    Task<string?> EvaluateAsync(string pluginName, string functionName, IDictionary<string, object?> arguments, CancellationToken ct);
}

/// <summary>Context returned by <see cref="IToolStatusNotifier.BeforeInvocationAsync"/>.</summary>
public sealed class ToolStatusContext
{
    public string? SessionId { get; init; }
    public string PluginName { get; init; } = "";
    public string FunctionName { get; init; } = "";
}

/// <summary>工具调用前后状态推送；由 <see cref="ToolStatusNotifier"/> 实现。</summary>
public interface IToolStatusNotifier
{
    Task<ToolStatusContext> BeforeInvocationAsync(string? sessionId, string pluginName, string functionName, IDictionary<string, object?> arguments, CancellationToken ct);
    Task AfterInvocationAsync(ToolStatusContext ctx, object? result, bool success, CancellationToken ct);
}
