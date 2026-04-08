using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Chat;

#pragma warning disable MAAI001 // Compaction API is experimental

namespace OfficeCopilot.Server;

public sealed partial class ChatService
{
    /// <summary>上下文阶段 Part1：预算 + MAF Compaction（摘要/工具结果折叠/截断）。记忆与知识库已迁移到 MAF <c>MessageAIContextProvider</c>。</summary>
    internal async Task RunStreamChatContextPhasePart1Async(StreamChatTurnContext turn, CancellationToken ct)
    {
        var sessionId = turn.SessionId;
        var sessionManagerForStatus = turn.SessionManager;
        var state = turn.State;
        var ctxConfig = turn.CtxConfig;

        var historyBudget = GetEffectiveMaxContextTokens()
            - ctxConfig.ReservedSystemTokens
            - ctxConfig.ReservedToolsTokens
            - ctxConfig.ReservedOutputTokens;

        if (historyBudget > 0 && !ctxConfig.PassThroughContext && ctxConfig.SummarizationEnabled && state.History.Count > 5)
        {
            var beforeCount = state.History.Count;
            try
            {
                await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在整理历史对话…", ct).ConfigureAwait(false);

                var chatClient = _runtime.GetChatClient();
                if (chatClient != null)
                {
                    var triggerTokens = (int)(historyBudget * ctxConfig.SummarizationTriggerRatio);
                    var strategy = BuildCompactionStrategy(chatClient, triggerTokens, historyBudget);
                    var compacted = await Microsoft.Agents.AI.Compaction.CompactionProvider.CompactAsync(
                        strategy, state.History, _logger, ct).ConfigureAwait(false);
                    var compactedList = compacted.ToList();
                    if (compactedList.Count < beforeCount)
                    {
                        state.History.Clear();
                        state.History.AddRange(compactedList);
                        var removed = beforeCount - compactedList.Count;
                        _logger.LogDebug("[{SessionId}] MAF Compaction removed {Removed} messages ({Before} → {After}).",
                            sessionId, removed, beforeCount, compactedList.Count);
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context",
                            $"历史压缩：{removed} 条消息已整理",
                            $"压缩前 {beforeCount} 条 → 压缩后 {compactedList.Count} 条（token 预算 {historyBudget}）", ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[{SessionId}] MAF Compaction failed, continuing without.", sessionId);
                var failTrace = AgentTraceFormatter.BuildContextSummarizationFailureTrace(ErrorMessageHelper.GetFriendlyMessage(ex));
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context", failTrace.Title, failTrace.Detail, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>构建 MAF <see cref="Microsoft.Agents.AI.Compaction.PipelineCompactionStrategy"/>：工具结果折叠 → 摘要 → 滑动窗口 → 截断兜底。</summary>
    private static Microsoft.Agents.AI.Compaction.CompactionStrategy BuildCompactionStrategy(
        IChatClient summarizerClient, int triggerTokens, int budgetTokens)
    {
        const string summarizationPrompt =
            "你是一个对话摘要助手。请将以下对话压缩为一段简短摘要，保留关键事实与结论，不超过 500 字。"
            + " 摘要中不要断言「当前文件里仍是…」「文档现在一定…」等磁盘实时状态；"
            + "优先概括用户曾提出的要求、助手曾执行的操作与结论，并可用「当时曾…」表述，避免读者把摘要当作此刻本机文件的真相。";

        return new Microsoft.Agents.AI.Compaction.PipelineCompactionStrategy(
            new Microsoft.Agents.AI.Compaction.ToolResultCompactionStrategy(
                Microsoft.Agents.AI.Compaction.CompactionTriggers.TokensExceed(triggerTokens)),
            new Microsoft.Agents.AI.Compaction.SummarizationCompactionStrategy(
                summarizerClient,
                Microsoft.Agents.AI.Compaction.CompactionTriggers.TokensExceed(triggerTokens),
                minimumPreservedGroups: 4,
                summarizationPrompt: summarizationPrompt),
            new Microsoft.Agents.AI.Compaction.TruncationCompactionStrategy(
                Microsoft.Agents.AI.Compaction.CompactionTriggers.TokensExceed(budgetTokens)));
    }

    /// <summary>上下文阶段 Part2：计划加载（供 MergePlanTools 使用）、AI-REQUEST 日志。跨端待办与计划内容注入已迁移到 MAF <c>MessageAIContextProvider</c>。</summary>
    internal async Task RunStreamChatContextPhasePart2Async(StreamChatTurnContext turn, CancellationToken ct)
    {
        var sessionId = turn.SessionId;
        var state = turn.State;
        var planId = turn.PlanId;

        turn.PlanResult = null;
        if (!string.IsNullOrWhiteSpace(planId))
        {
            turn.PlanResult = await _planStore.GetAsync(planId.Trim(), ct).ConfigureAwait(false);
        }

        var payloadChars = 0;
        for (var i = 0; i < state.History.Count; i++)
            payloadChars += (state.History[i].Text?.Length ?? 0);
        _logger.LogInformation(
            "[AI-REQUEST] SessionId={SessionId} phase=agent turns={Turns} payloadChars={PayloadChars}",
            sessionId, state.History.Count, payloadChars);
    }

    internal async Task RunStreamChatToolingPhaseAsync(StreamChatTurnContext turn, CancellationToken ct)
    {
        var sessionId = turn.SessionId;
        var userMessage = turn.UserMessage;
        var state = turn.State;
        var ctxConfig = turn.CtxConfig;
        var sessionManagerForStatus = turn.SessionManager;
        var planResult = turn.PlanResult;

        var clientType = sessionManagerForStatus.GetClientType(sessionId);
        IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs = null;
        var gateEnabled = ctxConfig.EnableToolNeedGate;
        await NotifyAgentStatusAsync(
            sessionManagerForStatus,
            sessionId,
            gateEnabled ? "正在判断本轮是否需要工具…" : "正在筛选可用工具…",
            ct).ConfigureAwait(false);
        _agentDebugStats.IncrementToolSelectionTotal();

        var recentHistory = state.History.Count > 1 ? state.History : null;
        try
        {
            ToolNeedGateResult gate;
            if (planResult != null)
                gate = new ToolNeedGateResult(true, "已加载计划，跳过工具需求门控。", false);
            else
                gate = await _toolSelector.EvaluateToolNeedGateAsync(userMessage, recentHistory, ct).ConfigureAwait(false);
            if (gate.InvokedLlm)
            {
                _agentDebugStats.RecordToolNeedGateLlmInvocation();
                if (!gate.BindTools)
                    _agentDebugStats.RecordToolNeedGateChatOnly();
            }

            // 门控关闭时不推 trace，避免时间线出现「已关闭」误导；有计划跳过门控或实际跑了门控 LLM 时再记录
            if (planResult != null || gateEnabled || gate.InvokedLlm)
            {
                await NotifyAgentTraceAsync(
                    sessionManagerForStatus,
                    sessionId,
                    "toolSelection",
                    "工具需求门控",
                    AgentTraceFormatter.TruncateDetail(gate.TraceDetail),
                    ct).ConfigureAwait(false);
            }

            if (!gate.BindTools)
            {
                _logger.LogInformation("[{SessionId}] ToolSelection: gate chose chat-only (no tools bound).", sessionId);
                FinalizeToolingPhaseTurn(turn, state, clientType, Array.Empty<AITool>(), selectedPairs: null, restrictedSubset: false);
                return;
            }

            if (gateEnabled)
                await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在筛选可用工具…", ct).ConfigureAwait(false);
            _logger.LogInformation("[{SessionId}] ToolSelection: gate passed, two-stage clientType={ClientType}.",
                sessionId, clientType ?? "(null)");
            _agentDebugStats.RecordTwoStageUsed();
            var twoStage = await _toolSelector.SelectFunctionsAsync(
                userMessage,
                recentHistory,
                _runtime.ToolRegistry,
                ct,
                new ToolSelectionContext(clientType)).ConfigureAwait(false);
            selectedPairs = twoStage.SelectedPairs;
            _logger.LogInformation("[{SessionId}] ToolSelection: two-stage returned selectedPairsCount={Count}.",
                sessionId, selectedPairs?.Count ?? -1);
            var tsTrace = AgentTraceFormatter.BuildTwoStageToolTrace(twoStage);
            await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", tsTrace.Title, tsTrace.Detail, ct).ConfigureAwait(false);

            var selectedTools = SessionToolResolver.ResolveToolsByClientType(_runtime.ToolRegistry, selectedPairs, clientType, sessionId);
            if (planResult != null && selectedTools != null)
                selectedTools = SessionToolResolver.MergePlanTools(_runtime.ToolRegistry, selectedTools);
            var restrictedSubset = selectedPairs is { Count: > 0 };
            var funcsCount = selectedTools?.Count ?? 0;
            _logger.LogInformation("[{SessionId}] ToolSelection: resolved functionCount={FuncCount} restrictedSubset={Restricted}.",
                sessionId, funcsCount, restrictedSubset);

            FinalizeToolingPhaseTurn(turn, state, clientType, selectedTools!, selectedPairs, restrictedSubset);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Tool selection failed, using all tools.", sessionId);
            _agentDebugStats.RecordToolSelectionException();
            selectedPairs = null;
            await NotifyAgentTraceAsync(
                sessionManagerForStatus, sessionId, "toolSelection", "工具选择异常，已回退全量工具",
                ErrorMessageHelper.GetFriendlyMessage(ex), ct).ConfigureAwait(false);

            var selectedTools = SessionToolResolver.ResolveToolsByClientType(_runtime.ToolRegistry, null, clientType, sessionId);
            if (planResult != null && selectedTools != null)
                selectedTools = SessionToolResolver.MergePlanTools(_runtime.ToolRegistry, selectedTools);
            FinalizeToolingPhaseTurn(turn, state, clientType, selectedTools!, selectedPairs: null, restrictedSubset: false);
        }
    }

    private void FinalizeToolingPhaseTurn(
        StreamChatTurnContext turn,
        SessionState state,
        string? clientType,
        IReadOnlyList<AITool> toolsForAgent,
        IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs,
        bool restrictedSubset)
    {
        var sessionId = turn.SessionId;
        var ctxConfig = turn.CtxConfig;
        var pairsCount = selectedPairs?.Count ?? 0;
        var funcsCount = toolsForAgent.Count;
        _logger.LogInformation("[{SessionId}] ToolSelection: finalize pairsCount={PairsCount} toolsForAgentCount={FuncCount} restricted={Restricted}.",
            sessionId, pairsCount, funcsCount, restrictedSubset);

        var maxOutputTokens = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
        turn.ExecSettings = new ChatOptions { MaxOutputTokens = maxOutputTokens };
        if (funcsCount > 0)
        {
            if (restrictedSubset)
                _logger.LogInformation("[{SessionId}] ToolSelection: MAF bound to {FunctionCount} functions (subset).", sessionId, funcsCount);
            else
                _logger.LogInformation("[{SessionId}] ToolSelection: MAF bound to {FunctionCount} functions (full allow-list).", sessionId, funcsCount);
        }
        else
            _logger.LogInformation("[{SessionId}] ToolSelection: MAF bound to zero tools (chat-only).", sessionId);

        turn.ToolsForAgentRound = toolsForAgent;
        turn.SelectedTools = funcsCount > 0 ? toolsForAgent : null;

        var activeModel = GetActiveModelEntry();
        turn.EnableSearchSuppressionSuffix = activeModel?.EnableSearch == true ? EnableSearchSuppressionInstruction : null;

        turn.IdentitySuffix = GetClientTypeIdentitySuffix(clientType);
        turn.HistoryToUse = BuildHistoryForStreamingTurn(state.History, turn.IdentitySuffix, turn.EnableSearchSuppressionSuffix);
    }

    /// <summary>将 Host 前言合并进本轮流式用历史的 system 首条，不写入持久 <see cref="SessionState.History"/>。</summary>
    private static List<ChatMessage> InjectHostPreambleIntoStreamingHistory(List<ChatMessage> baseHistory, string preamble)
    {
        if (string.IsNullOrWhiteSpace(preamble) || baseHistory.Count == 0) return baseHistory;
        if (baseHistory[0].Role != ChatRole.System) return baseHistory;
        var augmented = (baseHistory[0].Text ?? "") + "\n\n[Host 工作思路，仅本轮参考]\n" + preamble.Trim();
        var nh = new List<ChatMessage>(baseHistory.Count) { new(ChatRole.System, augmented) };
        for (var i = 1; i < baseHistory.Count; i++)
            nh.Add(baseHistory[i]);
        return nh;
    }
}
