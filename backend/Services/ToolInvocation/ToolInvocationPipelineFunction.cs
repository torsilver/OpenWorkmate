using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>
/// Wraps an <see cref="AIFunction"/> with the cross-cutting pipeline:
/// <see cref="ISecurityPipeline"/> → SessionContext injection → <see cref="IToolStatusNotifier"/>.
/// </summary>
internal sealed class ToolInvocationPipelineFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly string _pluginName;
    private readonly ToolInvocationPipelineServices _services;

    public ToolInvocationPipelineFunction(AIFunction inner, string pluginName, ToolInvocationPipelineServices services)
    {
        _inner = inner;
        _pluginName = pluginName;
        _services = services;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var funcName = _inner.Name ?? "?";
        var args = arguments;

        // --- SecurityFilter ---
        var ruleEffect = ToolPermissionRuleEvaluator.Evaluate(
            _services.ConfigService.Current.ToolPermissionRules, _pluginName, funcName);
        if (ruleEffect == ToolPermissionRuleEffect.Deny)
            return $"[系统拦截] 工具权限规则禁止调用 {_pluginName}.{funcName}。";

        var secResult = await _services.SecurityPipeline.EvaluateAsync(
            _pluginName, funcName, args, cancellationToken).ConfigureAwait(false);
        if (secResult != null)
            return secResult;

        // --- SessionContextFilter ---
        InjectSessionId(funcName, args);

        // --- ToolStatusFilter (pre) ---
        var sessionId = SessionContext.GetSessionId();
        var statusCtx = await _services.ToolStatus.BeforeInvocationAsync(
            sessionId, _pluginName, funcName, args, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

            // --- ToolStatusFilter (post success) ---
            await _services.ToolStatus.AfterInvocationAsync(
                statusCtx, result, success: true, cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            await _services.ToolStatus.AfterInvocationAsync(
                statusCtx, ErrorMessageHelper.GetFriendlyMessage(ex), success: false, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void InjectSessionId(string funcName, AIFunctionArguments args)
    {
        if (IsMeetingTranscriptFunction(funcName)) return;
        var sessionId = SessionContext.GetSessionId();
        if (!string.IsNullOrEmpty(sessionId))
            args["sessionId"] = sessionId;
    }

    private static bool IsMeetingTranscriptFunction(string? functionName) =>
        string.Equals(functionName, "meeting_transcript_read", StringComparison.OrdinalIgnoreCase)
        || string.Equals(functionName, "meeting_transcript_meta", StringComparison.OrdinalIgnoreCase);
}

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
