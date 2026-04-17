using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;
using OfficeCopilot.Server;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using OfficeCopilot.Server.Services.Telemetry;

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
            var rawName = context.Function?.Name ?? "?";
            string funcName;
            string pluginName;

            if (toolRegistry.TryGetPluginName(rawName, out var pnBare))
            {
                pluginName = pnBare;
                funcName = rawName;
                pipelineServices.Logger.LogDebug(
                    "[ToolInvoke] nameResolution route=bare rawName={RawName} plugin={Plugin} func={Func} (schema/OpenAPI 工具 name 应为裸函数名)",
                    rawName,
                    pluginName,
                    funcName);
            }
            else if (ToolQualifiedNameResolver.TryResolve(toolRegistry, rawName, out var pQual, out var bare, out var toolResolved)
                     && toolResolved is AIFunction afResolved)
            {
                pluginName = pQual;
                funcName = bare;
                if (!string.Equals(rawName, bare, StringComparison.Ordinal))
                {
                    pipelineServices.Logger.LogInformation(
                        "[ToolInvoke] nameResolution route=qualified_rewrite rawName={RawName} bareName={BareName} plugin={Plugin} (模型在 tool_calls 中使用了 Plugin.function；已映射为裸名并替换 AIFunction，推荐后续直接使用裸名 tool_calls)",
                        rawName,
                        bare,
                        pQual);
                    context.Function = afResolved;
                }
            }
            else
            {
                pluginName = "?";
                funcName = rawName;
                pipelineServices.Logger.LogWarning(
                    "[ToolInvoke] nameResolution route=unresolved rawName={RawName} (无法映射到注册表中的插件/函数；MEAI 可能报 Function not found，请核对 tool_calls.name 是否为 OpenAPI 中的裸函数名或有效 Plugin.function)",
                    rawName);
            }

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

                if (result is FunctionInvokingChatClient.FunctionInvocationResult firNotFound
                    && firNotFound.Status == FunctionInvokingChatClient.FunctionInvocationStatus.NotFound)
                {
                    var dts = DynamicToolingTurnScope.Current;
                    var mt = dts?.ToolListMutationTarget;
                    var mtCount = mt?.Count ?? -1;
                    var mtPreview = mt is null
                        ? "(null)"
                        : string.Join(
                            ",",
                            mt.Where(t => !string.IsNullOrEmpty(t.Name)).Select(t => t!.Name).Take(40));
                    if (mtPreview.Length > 520)
                        mtPreview = mtPreview[..520] + "…";
                    var activatedPreview = dts is null
                        ? "(no scope)"
                        : string.Join(
                            ",",
                            dts.ActivatedFunctionNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(32));
                    if (activatedPreview.Length > 520)
                        activatedPreview = activatedPreview[..520] + "…";
                    pipelineServices.Logger.LogWarning(
                        "[ToolInvoke] MEAI FunctionInvokingChatClient: tool not in current ChatOptions.Tools. rawName={RawName} plugin={Plugin} func={Func} mutationTargetCount={MtCount} mutationToolNamesPreview={MtPreview} activatedNamesPreview={ActivatedPreview}",
                        rawName,
                        pluginName,
                        funcName,
                        mtCount,
                        mtPreview,
                        activatedPreview);
                }

                var shouldRefreshDynamicToolsAfterActivate =
                    DynamicToolingActivateRefreshTriggers.ShouldRefreshChatOptionsToolsAfterInvocation(pluginName, funcName);
                pipelineServices.Logger.LogInformation(
                    "[ToolInvoke] completed rawToolCallName={RawName} resolvedPlugin={Plugin} resolvedFunc={Func} shouldRefreshDynamicToolsAfterActivate={ShouldRefresh} session={SessionId}",
                    rawName,
                    pluginName,
                    funcName,
                    shouldRefreshDynamicToolsAfterActivate,
                    SessionContext.GetSessionId() ?? "?");

                // activate_tools 后 ChatOptions.Tools 刷新见 AgentToolingPlugin（不依赖本中间件是否被 MAF 完整命中）。

                // 插件已执行：非 meta 工具（含 envelope 失败）均抑制「未 activate 则全量兜底」，避免二次写文档等。
                DynamicToolingTurnScope.Current?.MarkEffectfulNonMetaInvocation(funcName);

                // MEAI FunctionInvokingChatClient：工具内异常可能不抛，而以 FunctionInvocationResult 封装。
                if (ToolInvocationMeaiResultInspector.TryGetEnvelopeFailureMessage(
                        result, pluginName, funcName, out var envelopeMsg))
                {
                    await pipelineServices.ToolStatus.AfterInvocationAsync(
                        statusCtx, envelopeMsg, success: false, cancellationToken).ConfigureAwait(false);
                    EmitToolTelemetry(pipelineServices, sessionId, pluginName, funcName, success: false, envelopeMsg);
                    return envelopeMsg;
                }

                var payload = ToolInvocationMeaiResultInspector.GetEffectivePayload(result);
                var normalized = ToolStatusNotifier.NormalizeToolResultToString(payload);
                var invocationOk = !ToolSemanticFailureMarkers.LooksLikeSemanticFailure(normalized);
                await pipelineServices.ToolStatus.AfterInvocationAsync(
                    statusCtx, payload, invocationOk, cancellationToken).ConfigureAwait(false);
                EmitToolTelemetry(pipelineServices, sessionId, pluginName, funcName, invocationOk, normalized);
                if (invocationOk)
                    ToolCatalogSuccessBoost.RecordSuccess(funcName);
                return payload;
            }
            catch (JsonException jsonEx)
            {
                var toolMsg =
                    $"[参数绑定失败] 工具 {pluginName}.{funcName} 的 JSON 参数与期望类型不一致（例如布尔请用 JSON 的 true/false，勿写成带引号的字符串 \"true\"/\"false\"）。详情：{jsonEx.Message}";
                pipelineServices.Logger.LogWarning(
                    jsonEx,
                    "[ToolInvoke] JSON parameter binding failed plugin={Plugin} func={Func} rawName={RawName} argsPreview={ArgsPreview}",
                    pluginName,
                    funcName,
                    rawName,
                    FormatArgumentsForDiagnosticLog(args));
                await pipelineServices.ToolStatus.AfterInvocationAsync(
                    statusCtx, toolMsg, success: false, cancellationToken).ConfigureAwait(false);
                EmitToolTelemetry(pipelineServices, sessionId, pluginName, funcName, success: false, toolMsg);
                return toolMsg;
            }
            catch (Exception ex) when (ToolInvocationFailureFormatter.ShouldRethrowAsCancellation(ex, cancellationToken))
            {
                throw;
            }
            catch (Exception ex)
            {
                var toolMsg = ToolInvocationFailureFormatter.FormatToolInvocationFailure(pluginName, funcName, ex);
                pipelineServices.Logger.LogWarning(
                    ex,
                    "[ToolInvoke] Tool failed plugin={Plugin} func={Func} returnToModelLen={Len}",
                    pluginName,
                    funcName,
                    toolMsg.Length);
                await pipelineServices.ToolStatus.AfterInvocationAsync(
                    statusCtx, toolMsg, success: false, cancellationToken).ConfigureAwait(false);
                EmitToolTelemetry(pipelineServices, sessionId, pluginName, funcName, success: false, toolMsg);
                return toolMsg;
            }
        };
    }

    private static string FormatArgumentsForDiagnosticLog(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
            return "";
        try
        {
            var s = JsonSerializer.Serialize(args, Utf8JsonFileOptions.Compact);
            const int max = 4000;
            if (s.Length > max)
                return s[..max] + "…";
            return s;
        }
        catch (Exception ex)
        {
            return string.Create(CultureInfo.InvariantCulture, $"[serialize failed: {ex.Message}] ")
                + string.Join("; ", args.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private static void EmitToolTelemetry(
        ToolInvocationPipelineServices pipelineServices,
        string? sessionId,
        string pluginName,
        string funcName,
        bool success,
        string? resultSummary)
    {
        var msg = $"{pluginName}.{funcName} success={success} len={(resultSummary ?? "").Length}";
        pipelineServices.TelemetryRelay?.TryEnqueueFromSession(
            pipelineServices.ConfigService,
            pipelineServices.TelemetryTransmissionPolicy,
            pipelineServices.SessionManager,
            sessionId,
            "tool_invocation_end",
            "p0",
            msg);
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
