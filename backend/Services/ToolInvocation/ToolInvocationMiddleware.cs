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
                    pipelineServices.Logger.LogWarning(
                        "[ToolInvoke] MEAI FunctionInvokingChatClient: tool not in current ChatOptions.Tools. rawName={RawName} plugin={Plugin} func={Func}. Dynamic tooling: call activate_tools with this bare name before tool_calls.",
                        rawName,
                        pluginName,
                        funcName);
                }

                TryRefreshDynamicToolingToolsAfterActivate(toolRegistry, funcName, pipelineServices.Logger);

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

    /// <summary>
    /// 同一次 <c>RunStreamingAsync</c> 内，<c>activate_tools</c> 会更新 <see cref="DynamicToolingTurnState.ActivatedFunctionNames"/>，
    /// 但 <see cref="ChatOptions.Tools"/> 仍指向 pass 开始时的列表；此处原地同步，使下一跳模型请求能调用新激活的工具。
    /// </summary>
    private static void TryRefreshDynamicToolingToolsAfterActivate(ToolRegistry registry, string funcName, ILogger log)
    {
        if (!string.Equals(funcName, DynamicToolingConstants.ActivateFunctionName, StringComparison.OrdinalIgnoreCase))
            return;

        var dts = DynamicToolingTurnScope.Current;
        if (dts is null)
        {
            log.LogDebug("[DynamicTools] activate_tools finished but DynamicToolingTurnScope.Current is null (not a dynamic-tooling pass)");
            return;
        }

        var target = dts.ToolListMutationTarget;
        if (target is null)
        {
            log.LogWarning(
                "[DynamicTools] activate_tools finished but ToolListMutationTarget is missing; ChatOptions.Tools will NOT refresh until next MAF outer pass. session={SessionId} clientType={ClientType}",
                dts.SessionIdForTools ?? SessionContext.GetSessionId() ?? "?",
                dts.ClientTypeForTools ?? "?");
            return;
        }

        var beforeCount = target.Count;
        var fresh = SessionToolResolver.BuildDynamicActiveToolList(
            registry,
            dts,
            dts.ClientTypeForTools,
            dts.SessionIdForTools,
            dts.MergePlanIntoDynamicBootstrap);

        var activatedSorted = dts.ActivatedFunctionNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var activatedPreview = string.Join(',', activatedSorted);
        if (activatedPreview.Length > 480)
            activatedPreview = activatedPreview[..480] + "…";

        var nameList = fresh.Select(t => t.Name ?? "?").ToArray();
        var preview = string.Join(',', nameList.Take(28));
        if (nameList.Length > 28)
            preview += ",…";

        target.Clear();
        target.AddRange(fresh);

        // 已在同一 RunStreamingAsync 内刷新 ChatOptions.Tools，后续模型请求会带上新工具；不必再开外层 RunSinglePass（否则多一次 Agent 会话，模型常从头再跑浏览器/检索）。
        dts.ClearExpansionFlag();

        log.LogInformation(
            "[DynamicTools] ChatOptions.Tools refreshed after activate_tools session={SessionId} clientType={ClientType} mergePlan={MergePlan} beforeCount={Before} afterCount={After} activatedCount={ActivatedCount} activated={ActivatedPreview} toolsPreview={ToolsPreview}",
            dts.SessionIdForTools ?? SessionContext.GetSessionId() ?? "?",
            dts.ClientTypeForTools ?? "?",
            dts.MergePlanIntoDynamicBootstrap,
            beforeCount,
            fresh.Count,
            activatedSorted.Length,
            activatedPreview,
            preview);
    }
}
