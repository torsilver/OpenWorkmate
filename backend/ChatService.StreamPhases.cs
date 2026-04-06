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
        await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在筛选可用工具…", ct).ConfigureAwait(false);
        _agentDebugStats.IncrementToolSelectionTotal();
        try
        {
            var recentHistory = state.History.Count > 1 ? state.History : null;
            var embeddingConfigured = _embeddingProvider.IsConfigured;
            var storePersistent = _vectorStore.IsPersistent;
            _logger.LogInformation("[{SessionId}] ToolSelection: entry clientType={ClientType} embeddingConfigured={Emb} storePersistent={Store}.",
                sessionId, clientType ?? "(null)", embeddingConfigured, storePersistent);
            if (embeddingConfigured && storePersistent)
            {
                var userPrompt = BuildToolSelectionUserPrompt(userMessage, recentHistory);
                var vectorSearch = await _toolIndex.SearchToolsAsync(
                    userPrompt, clientType,
                    topK: ctxConfig.ToolSearchTopK,
                    minScore: ctxConfig.ToolSearchMinScore,
                    minCount: ctxConfig.ToolSearchMinCount,
                    ct).ConfigureAwait(false);
                _logger.LogInformation("[{SessionId}] ToolSelection: vector search result count={Count} goodEnough={GoodEnough}.",
                    sessionId, vectorSearch.Results.Count, vectorSearch.GoodEnough);
                var scored = vectorSearch.ScoredHits;
                var maxS = scored.Count > 0 ? scored[0].Score : 0.0;
                double? secondS = scored.Count >= 2 ? scored[1].Score : null;
                _agentDebugStats.RecordVectorSearchCompleted(new VectorSearchTelemetry(
                    clientType,
                    maxS,
                    secondS,
                    scored.Count,
                    vectorSearch.GoodEnough,
                    VectorFirstPathChosen: false));
                var vectorToolHint = BuildVectorToolHintForStage1(vectorSearch.Results, Math.Clamp(ctxConfig.ToolSearchTopK, 1, 20));
                var vectorDecision =
                    "决策：向量结果仅作 stage1 参考；本轮工具集一律由两阶段子类选择决定（已禁用向量 sole）。goodEnough="
                    + vectorSearch.GoodEnough + "。";
                if (!string.IsNullOrEmpty(vectorToolHint))
                    vectorDecision += " 已向 stage1 注入向量参考列表。";
                var vectorDetail = AgentTraceFormatter.BuildToolVectorSearchDetail(
                    clientType, vectorSearch,
                    ctxConfig.ToolSearchTopK, ctxConfig.ToolSearchMinScore, ctxConfig.ToolSearchMinCount,
                    vectorDecision);
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", "工具选择：向量索引检索", vectorDetail, ct).ConfigureAwait(false);

                _agentDebugStats.RecordTwoStageUsed();
                _logger.LogInformation("[{SessionId}] ToolSelection: two-stage LLM path (vector never sole).", sessionId);
                var twoStage = await _toolSelector.SelectFunctionsAsync(
                    userMessage,
                    recentHistory,
                    _runtime.ToolRegistry,
                    ct,
                    new ToolSelectionContext(vectorToolHint, clientType)).ConfigureAwait(false);
                selectedPairs = twoStage.SelectedPairs;
                _agentDebugStats.RecordVectorThenTwoStageOutcome(selectedPairs == null);
                _logger.LogInformation("[{SessionId}] ToolSelection: two-stage returned selectedPairsCount={Count}.",
                    sessionId, selectedPairs?.Count ?? -1);
                var tsTrace = AgentTraceFormatter.BuildTwoStageToolTrace(twoStage);
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", tsTrace.Title, tsTrace.Detail, ct).ConfigureAwait(false);
            }
            else
            {
                if (!embeddingConfigured)
                    _agentDebugStats.RecordVectorSkippedNoEmbedding();
                else
                    _agentDebugStats.RecordVectorSkippedNonPersistent();
                var skipDetail = AgentTraceFormatter.BuildToolVectorSkipDetail(embeddingConfigured, storePersistent);
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", "工具选择：向量索引未使用", skipDetail, ct).ConfigureAwait(false);

                _agentDebugStats.RecordTwoStageUsed();
                _logger.LogInformation("[{SessionId}] ToolSelection: two-stage LLM path (no vector index).", sessionId);
                var twoStage = await _toolSelector.SelectFunctionsAsync(
                    userMessage,
                    recentHistory,
                    _runtime.ToolRegistry,
                    ct,
                    new ToolSelectionContext(null, clientType)).ConfigureAwait(false);
                selectedPairs = twoStage.SelectedPairs;
                _logger.LogInformation("[{SessionId}] ToolSelection: two-stage returned selectedPairsCount={Count}.",
                    sessionId, selectedPairs?.Count ?? -1);
                var tsTrace = AgentTraceFormatter.BuildTwoStageToolTrace(twoStage);
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", tsTrace.Title, tsTrace.Detail, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Tool selection failed, using all tools.", sessionId);
            _agentDebugStats.RecordToolSelectionException();
            selectedPairs = null;
            await NotifyAgentTraceAsync(
                sessionManagerForStatus, sessionId, "toolSelection", "工具选择异常，已回退全量工具",
                ErrorMessageHelper.GetFriendlyMessage(ex), ct).ConfigureAwait(false);
        }

        IReadOnlyList<AITool>? selectedTools;
        selectedTools = ResolveToolsByClientType(_runtime.ToolRegistry, selectedPairs, clientType, sessionId);
        if (planResult != null && selectedTools != null)
            selectedTools = MergePlanTools(_runtime.ToolRegistry, selectedTools);
        var pairsCount = selectedPairs?.Count ?? 0;
        var funcsCount = selectedTools?.Count ?? 0;
        var useAllTools = selectedTools == null || selectedTools.Count == 0;
        _logger.LogInformation("[{SessionId}] ToolSelection: ResolveFunctionsByClientType clientType={ClientType} selectedPairsCount={PairsCount} resolvedFunctionCount={FuncCount} useAllTools={UseAll}.",
            sessionId, clientType ?? "(null)", pairsCount, funcsCount, useAllTools);

        var maxOutputTokens = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
        if (selectedTools is { Count: > 0 })
        {
            turn.ExecSettings = new ChatOptions
            {
                MaxOutputTokens = maxOutputTokens,
            };
            _logger.LogInformation("[{SessionId}] ToolSelection: final restricted to {FunctionCount} functions clientType={ClientType}.",
                sessionId, selectedTools.Count, clientType ?? "(null)");
            _logger.LogDebug("[{SessionId}] Tool selection: clientType={ClientType} {FunctionCount} functions",
                sessionId, clientType ?? "(null)", selectedTools.Count);
        }
        else
        {
            turn.ExecSettings = new ChatOptions
            {
                MaxOutputTokens = maxOutputTokens,
            };
            _logger.LogInformation("[{SessionId}] ToolSelection: final no restriction (all tools).", sessionId);
        }

        turn.SelectedTools = selectedTools is { Count: > 0 } ? selectedTools : null;

        turn.IdentitySuffix = GetClientTypeIdentitySuffix(clientType);
        turn.HistoryToUse = BuildHistoryForStreamingTurn(state.History, turn.IdentitySuffix);
    }

    private static string? BuildVectorToolHintForStage1(
        IReadOnlyList<(string PluginName, string FunctionName)> results,
        int maxItems)
    {
        if (results == null || results.Count == 0 || maxItems <= 0)
            return null;
        var lines = results.Take(maxItems).Select(p => $"- {p.PluginName}.{p.FunctionName}");
        return "[向量索引参考，仅供子类选择时参考；须结合对话与历史判断]\n" + string.Join("\n", lines);
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
