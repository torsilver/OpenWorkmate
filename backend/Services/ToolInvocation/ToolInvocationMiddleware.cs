using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>
/// MAF Function Calling Middleware：替代旧的 <c>ToolInvocationPipelineFunction</c>（AIFunction 包装器）。
/// 通过 <c>agent.AsBuilder().Use(middleware).Build()</c> 注册到 <see cref="ChatClientAgent"/> 管道中，
/// 框架对每次工具调用自动执行此中间件，无需手动包装工具。
/// </summary>
public static class ToolInvocationMiddleware
{
    /// <summary>
    /// 创建一个绑定了 <paramref name="toolRegistry"/> 和 <paramref name="pipelineServices"/> 的 MAF function calling middleware 委托。
    /// </summary>
    public static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>>
        Create(ToolRegistry toolRegistry, ToolInvocationPipelineServices pipelineServices)
    {
        return async (agent, context, next, cancellationToken) =>
        {
            var funcName = context.Function?.Name ?? "?";
            var pluginName = toolRegistry.TryGetPluginName(funcName, out var pn) ? pn : "?";

            // --- Permission rule (fast path: Deny blocks all tools) ---
            var ruleEffect = ToolPermissionRuleEvaluator.Evaluate(
                pipelineServices.ConfigService.Current.ToolPermissionRules, pluginName, funcName);
            if (ruleEffect == ToolPermissionRuleEffect.Deny)
            {
                context.Terminate = true;
                return $"[系统拦截] 工具权限规则禁止调用 {pluginName}.{funcName}。";
            }

            // --- Security pipeline (HITL / allowlist) ---
            var args = (IDictionary<string, object?>)context.Arguments;
            var secResult = await pipelineServices.SecurityPipeline.EvaluateAsync(
                pluginName, funcName, args, cancellationToken).ConfigureAwait(false);
            if (secResult != null)
            {
                context.Terminate = true;
                return secResult;
            }

            // --- SessionContext injection ---
            InjectSessionId(funcName, args);

            // --- ToolStatus (pre) ---
            var sessionId = SessionContext.GetSessionId();
            var statusCtx = await pipelineServices.ToolStatus.BeforeInvocationAsync(
                sessionId, pluginName, funcName, args, cancellationToken).ConfigureAwait(false);

            try
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);

                await pipelineServices.ToolStatus.AfterInvocationAsync(
                    statusCtx, result, success: true, cancellationToken).ConfigureAwait(false);

                return result;
            }
            catch (JsonException jsonEx)
            {
                var toolMsg =
                    $"[参数绑定失败] 工具 {pluginName}.{funcName} 的 JSON 参数与期望类型不一致（例如布尔请用 JSON 的 true/false，勿写成带引号的字符串 \"true\"/\"false\"）。详情：{jsonEx.Message}";
                await pipelineServices.ToolStatus.AfterInvocationAsync(
                    statusCtx, toolMsg, success: false, cancellationToken).ConfigureAwait(false);
                return toolMsg;
            }
            catch (Exception ex)
            {
                await pipelineServices.ToolStatus.AfterInvocationAsync(
                    statusCtx, ErrorMessageHelper.GetFriendlyMessage(ex), success: false, cancellationToken).ConfigureAwait(false);
                throw;
            }
        };
    }

    private static void InjectSessionId(string funcName, IDictionary<string, object?> args)
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
