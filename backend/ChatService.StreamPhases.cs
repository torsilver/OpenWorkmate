using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.CrossAgentTask;
using OfficeCopilot.Server.Services.Memory;
using OfficeCopilot.Server.Services.Plan;
using OfficeCopilot.Server.Services.SemanticKernel;

namespace OfficeCopilot.Server;

public sealed partial class ChatService
{
    /// <summary>上下文阶段 Part1：预算/摘要/截断、记忆与知识库；警告写入 <see cref="StreamChatTurnContext.ContextWarnings"/>（在 Part2 之前由调用方 yield）。</summary>
    internal async Task RunStreamChatContextPhasePart1Async(StreamChatTurnContext turn, CancellationToken ct)
    {
        var sessionId = turn.SessionId;
        var sessionManagerForStatus = turn.SessionManager;
        var state = turn.State;
        var kernel = turn.Kernel;
        var chat = turn.Chat;
        var ctxConfig = turn.CtxConfig;
        var userMessage = turn.UserMessage;
        var knowledgeBaseId = turn.KnowledgeBaseId;

        var historyBudget = GetEffectiveMaxContextTokens()
            - ctxConfig.ReservedSystemTokens
            - ctxConfig.ReservedToolsTokens
            - ctxConfig.ReservedOutputTokens;

        if (historyBudget > 0 && !ctxConfig.PassThroughContext)
        {
            var totalTokens = EstimateHistoryTokens(state.History, ctxConfig);

            var summarized = false;
            if (ctxConfig.SummarizationEnabled && state.History.Count > 5
                && totalTokens >= (int)(historyBudget * ctxConfig.SummarizationTriggerRatio))
            {
                try
                {
                    await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在整理历史对话…", ct).ConfigureAwait(false);
                    var sumResult = await TrySummarizeOldTurnsAsync(state.History, kernel, chat, ctxConfig, sessionId, ct).ConfigureAwait(false);
                    totalTokens = EstimateHistoryTokens(state.History, ctxConfig);
                    summarized = sumResult.DidCompact;
                    if (sumResult.DidCompact)
                    {
                        var offloadDir = GetConversationHistoryDirectory(ctxConfig);
                        var offloadConfigured = !string.IsNullOrWhiteSpace(offloadDir);
                        var ctxTrace = AgentTraceFormatter.BuildContextSummarizationSuccessTrace(
                            sumResult.MessagesRemoved, sumResult.SummaryLength, ctxConfig, offloadConfigured);
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context", ctxTrace.Title, ctxTrace.Detail, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{SessionId}] Summarization failed, continuing without.", sessionId);
                    var failTrace = AgentTraceFormatter.BuildContextSummarizationFailureTrace(ErrorMessageHelper.GetFriendlyMessage(ex));
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context", failTrace.Title, failTrace.Detail, ct).ConfigureAwait(false);
                }
            }

            if (!summarized && ctxConfig.TruncateToolArgsThresholdRatio > 0 && ctxConfig.TruncateToolArgsMaxChars > 0
                && totalTokens >= (int)(historyBudget * ctxConfig.TruncateToolArgsThresholdRatio))
            {
                var keep = Math.Max(0, ctxConfig.TruncateToolArgsKeepMessages);
                var maxChars = Math.Max(100, ctxConfig.TruncateToolArgsMaxChars);
                var truncateSuffix = "…(已截断)";
                var oldEndIndex = Math.Max(0, state.History.Count - keep - 1);
                var truncatedCount = 0;
                for (var i = 1; i <= oldEndIndex && i < state.History.Count; i++)
                {
                    var msg = state.History[i];
                    var content = msg.Content ?? "";
                    if (content.Length <= maxChars) continue;
                    var truncated = content.AsSpan(0, maxChars).ToString() + truncateSuffix;
                    state.History[i] = new ChatMessageContent(msg.Role, truncated);
                    truncatedCount++;
                }
                if (truncatedCount > 0)
                {
                    var trTrace = AgentTraceFormatter.BuildContextTruncateTrace(
                        truncatedCount, keep, maxChars, ctxConfig.TruncateToolArgsThresholdRatio, totalTokens, historyBudget);
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context", trTrace.Title, trTrace.Detail, ct).ConfigureAwait(false);
                }
            }
        }

        var memorySvc = _serviceProvider.GetService<IMemoryStoreService>();
        if (memorySvc?.IsAvailable == true && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
        {
            try
            {
                await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在检索相关记忆…", ct).ConfigureAwait(false);
                var sessionTopK = Math.Clamp(ctxConfig.MemorySessionTopK, 1, 20);
                var sharedTopK = Math.Clamp(ctxConfig.MemorySharedTopK, 1, 20);
                var sessionResults = await memorySvc.SearchAsync(userMessage, sessionTopK, sessionId, ct).ConfigureAwait(false);
                var sharedResults = await memorySvc.SearchSharedAsync(userMessage, sharedTopK, ct).ConfigureAwait(false);
                var memTrace = AgentTraceFormatter.BuildMemoryTrace(sessionResults, sharedResults, sessionTopK, sharedTopK);
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "memory", memTrace.Title, memTrace.Detail, ct).ConfigureAwait(false);
                if (sessionResults.Count > 0 || sharedResults.Count > 0)
                {
                    var parts = new List<string>();
                    if (sessionResults.Count > 0)
                        parts.Add("[以下是与当前对话相关的长期记忆，供参考]\n[本会话记忆]\n" + string.Join("\n", sessionResults.Select(r => "- " + r.Text)));
                    if (sharedResults.Count > 0)
                        parts.Add("[来自共享记忆]\n" + string.Join("\n", sharedResults.Select(r => "- " + r.Text)));
                    var memoryBlock = string.Join("\n\n", parts);
                    if (ctxConfig.MemoryInjectionMaxChars > 0 && memoryBlock.Length > ctxConfig.MemoryInjectionMaxChars)
                        memoryBlock = memoryBlock.AsSpan(0, ctxConfig.MemoryInjectionMaxChars).ToString() + "\n（前文已截断）";
                    var currentSystem = state.History[0].Content ?? "";
                    state.History.RemoveAt(0);
                    state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem + "\n\n" + memoryBlock));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[{SessionId}] Memory search failed, continuing without injection.", sessionId);
                var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
                turn.ContextWarnings.Add("记忆检索失败：" + friendly + " 当前对话未注入长期记忆。");
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "memory", "长期记忆检索失败", friendly, ct).ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrWhiteSpace(knowledgeBaseId) && memorySvc?.IsAvailable == true && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
        {
            try
            {
                await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在检索知识库…", ct).ConfigureAwait(false);
                var kbResults = await memorySvc.SearchKnowledgeBaseAsync(knowledgeBaseId!.Trim(), userMessage, 5, ct).ConfigureAwait(false);
                var kbTrace = AgentTraceFormatter.BuildKnowledgeBaseTrace(knowledgeBaseId!.Trim(), kbResults);
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "knowledgeBase", kbTrace.Title, kbTrace.Detail, ct).ConfigureAwait(false);
                if (kbResults.Count > 0)
                {
                    var kbLines = kbResults.Select(r => $"- {r.Text}").ToList();
                    var kbBlock = "[以下来自知识库的参考内容]\n" + string.Join("\n", kbLines);
                    var currentSystem = state.History[0].Content ?? "";
                    state.History.RemoveAt(0);
                    state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem + "\n\n" + kbBlock));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[{SessionId}] Knowledge base search failed for {KbId}.", sessionId, knowledgeBaseId);
                var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
                turn.ContextWarnings.Add("知识库检索失败：" + friendly);
                await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "knowledgeBase", "知识库检索失败", friendly, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>上下文阶段 Part2：跨端待办、计划/plan 注入、AI-REQUEST 日志。</summary>
    internal async Task RunStreamChatContextPhasePart2Async(StreamChatTurnContext turn, CancellationToken ct)
    {
        var sessionId = turn.SessionId;
        var state = turn.State;
        var mode = turn.Mode;
        var planId = turn.PlanId;
        var planCurrentStepIndex = turn.PlanCurrentStepIndex;
        var ctxConfig = turn.CtxConfig;

        var taskStore = _serviceProvider.GetService<ICrossAgentTaskStore>();
        var sessionManagerForTask = _serviceProvider.GetService<SessionManager>();
        if (taskStore != null && sessionManagerForTask != null && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
        {
            try
            {
                var clientTypeForTask = sessionManagerForTask.GetClientType(sessionId);
                var pending = await taskStore.GetPendingForTargetAsync(clientTypeForTask, sessionId, ct).ConfigureAwait(false);
                if (pending.Count > 0)
                {
                    var taskLines = pending.Select(t => $"- [id={t.Id}] {t.Description}").ToList();
                    var taskBlock = "[以下来自其他端的待办，请在本轮完成并调用 complete_cross_agent_task 标记完成]\n" + string.Join("\n", taskLines);
                    var currentSystem = state.History[0].Content ?? "";
                    state.History.RemoveAt(0);
                    state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem + "\n\n" + taskBlock));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[{SessionId}] Cross-agent task pull failed.", sessionId);
            }
        }

        turn.IsPlanMode = string.Equals(mode?.Trim(), "plan", StringComparison.OrdinalIgnoreCase);
        turn.PlanResult = null;
        if (state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
        {
            var currentSystem = state.History[0].Content ?? "";
            var systemModified = false;
            if (turn.IsPlanMode)
            {
                currentSystem += "\n\n[当前为计划模式] 请根据用户描述仅生成实现计划（Markdown），并调用 create_plan 工具保存。不要执行具体操作，不要调用其他工具。";
                systemModified = true;
            }
            if (!string.IsNullOrWhiteSpace(planId) && !turn.IsPlanMode)
            {
                turn.PlanResult = await _planStore.GetAsync(planId.Trim(), ct).ConfigureAwait(false);
                if (turn.PlanResult != null)
                {
                    var planContent = turn.PlanResult.Value.Content;
                    var stepIndex = planCurrentStepIndex is > 0 ? planCurrentStepIndex.Value : 1;
                    var stepOnly = PlanStepParser.GetStepAt(planContent, stepIndex);
                    if (!string.IsNullOrWhiteSpace(stepOnly))
                    {
                        currentSystem += "\n\n[当前绑定的计划·第 " + stepIndex + " 步]\n" + stepOnly;
                    }
                    else
                    {
                        var planMaxChars = ctxConfig.PlanContentMaxChars;
                        if (planMaxChars > 0 && planContent.Length > planMaxChars)
                            planContent = planContent.AsSpan(0, planMaxChars).ToString() + "\n（前文已截断）";
                        currentSystem += "\n\n[当前绑定的计划]\n" + planContent;
                    }
                    systemModified = true;
                }
            }
            if (systemModified)
            {
                state.History.RemoveAt(0);
                state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem));
            }
        }

        var payloadChars = 0;
        for (var i = 0; i < state.History.Count; i++)
            payloadChars += (state.History[i].Content?.Length ?? 0);
        var phase = turn.IsPlanMode ? "plan" : "agent";
        _logger.LogInformation(
            "[AI-REQUEST] SessionId={SessionId} phase={Phase} turns={Turns} payloadChars={PayloadChars}",
            sessionId, phase, state.History.Count, payloadChars);
    }

    internal async Task RunStreamChatToolingPhaseAsync(StreamChatTurnContext turn, CancellationToken ct)
    {
        var sessionId = turn.SessionId;
        var userMessage = turn.UserMessage;
        var state = turn.State;
        var kernel = turn.Kernel;
        var ctxConfig = turn.CtxConfig;
        var sessionManagerForStatus = turn.SessionManager;
        var isPlanMode = turn.IsPlanMode;
        var planResult = turn.PlanResult;

        var aiConfig = _configService.Current.AI;
        var clientType = sessionManagerForStatus.GetClientType(sessionId);
        IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs = null;
        if (!isPlanMode)
        {
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
                    var vectorFirstChosen = vectorSearch.GoodEnough && vectorSearch.Results.Count > 0;
                    var scored = vectorSearch.ScoredHits;
                    var maxS = scored.Count > 0 ? scored[0].Score : 0.0;
                    double? secondS = scored.Count >= 2 ? scored[1].Score : null;
                    _agentDebugStats.RecordVectorSearchCompleted(new VectorSearchTelemetry(
                        clientType,
                        maxS,
                        secondS,
                        scored.Count,
                        vectorSearch.GoodEnough,
                        vectorFirstChosen));
                    string vectorDecision;
                    if (vectorFirstChosen)
                    {
                        selectedPairs = MergeVectorResultsWithAlwaysInclude(vectorSearch.Results, aiConfig ?? new AiConfig(), kernel);
                        _logger.LogInformation("[{SessionId}] ToolSelection: using vector-first path, selectedPairsCount={Count}.", sessionId, selectedPairs.Count);
                        _logger.LogDebug("[{SessionId}] Tool selection: vector-first used, {Count} tools.", sessionId, selectedPairs.Count);
                        vectorDecision = "决策：已采用向量优先路径（合并 AlwaysInclude 后 (插件,函数) 对数=" + selectedPairs.Count + "）。";
                    }
                    else
                    {
                        vectorDecision = "决策：向量命中未达 goodEnough 或为空，将调用两阶段子类筛选。";
                    }
                    var vectorDetail = AgentTraceFormatter.BuildToolVectorSearchDetail(
                        clientType, vectorSearch,
                        ctxConfig.ToolSearchTopK, ctxConfig.ToolSearchMinScore, ctxConfig.ToolSearchMinCount,
                        vectorDecision);
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", "工具选择：向量索引检索", vectorDetail, ct).ConfigureAwait(false);
                }
                else
                {
                    if (!embeddingConfigured)
                        _agentDebugStats.RecordVectorSkippedNoEmbedding();
                    else
                        _agentDebugStats.RecordVectorSkippedNonPersistent();
                    var skipDetail = AgentTraceFormatter.BuildToolVectorSkipDetail(embeddingConfigured, storePersistent);
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", "工具选择：向量索引未使用", skipDetail, ct).ConfigureAwait(false);
                }
                if (selectedPairs == null)
                {
                    _agentDebugStats.RecordTwoStageUsed();
                    _logger.LogInformation("[{SessionId}] ToolSelection: using two-stage LLM path.", sessionId);
                    var twoStage = await _toolSelector.SelectFunctionsAsync(userMessage, recentHistory, kernel, ct).ConfigureAwait(false);
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
        }
        IReadOnlyList<KernelFunction>? selectedFunctions;
        if (isPlanMode)
        {
            selectedFunctions = GetPlanOnlyFunctions(kernel);
            _logger.LogInformation("[{SessionId}] ToolSelection: plan mode, planOnlyFunctionCount={Count}.", sessionId, selectedFunctions?.Count ?? 0);
        }
        else
        {
            selectedFunctions = ResolveFunctionsByClientType(kernel, selectedPairs, clientType, sessionId);
            if (planResult != null && selectedFunctions != null)
                selectedFunctions = MergePlanFunctions(kernel, selectedFunctions);
            var pairsCount = selectedPairs?.Count ?? 0;
            var funcsCount = selectedFunctions?.Count ?? 0;
            var useAllTools = selectedFunctions == null || selectedFunctions.Count == 0;
            _logger.LogInformation("[{SessionId}] ToolSelection: ResolveFunctionsByClientType clientType={ClientType} selectedPairsCount={PairsCount} resolvedFunctionCount={FuncCount} useAllTools={UseAll}.",
                sessionId, clientType ?? "(null)", pairsCount, funcsCount, useAllTools);
        }

        var maxOutputTokens = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
        if (selectedFunctions is { Count: > 0 })
        {
            turn.ExecSettings = new OpenAIPromptExecutionSettings
            {
                MaxTokens = maxOutputTokens,
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(selectedFunctions)
            };
            _logger.LogInformation("[{SessionId}] ToolSelection: final restricted to {FunctionCount} functions clientType={ClientType}.",
                sessionId, selectedFunctions.Count, clientType ?? "(null)");
            _logger.LogDebug("[{SessionId}] Tool selection: clientType={ClientType} {FunctionCount} functions",
                sessionId, clientType ?? "(null)", selectedFunctions.Count);
        }
        else
        {
            turn.ExecSettings = new OpenAIPromptExecutionSettings
            {
                MaxTokens = maxOutputTokens,
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };
            _logger.LogInformation("[{SessionId}] ToolSelection: final no restriction (all tools).", sessionId);
        }

        turn.IdentitySuffix = GetClientTypeIdentitySuffix(clientType);
        turn.HistoryToUse = BuildHistoryForStreamingTurn(state.History, turn.IdentitySuffix, isPlanMode);
    }

    /// <summary>将 Host 前言合并进本轮流式用历史的 system 首条，不写入持久 <see cref="SessionState.History"/>。</summary>
    private static ChatHistory InjectHostPreambleIntoStreamingHistory(ChatHistory baseHistory, string preamble)
    {
        if (string.IsNullOrWhiteSpace(preamble) || baseHistory.Count == 0) return baseHistory;
        if (baseHistory[0].Role != AuthorRole.System) return baseHistory;
        var augmented = (baseHistory[0].Content ?? "") + "\n\n[Host 工作思路，仅本轮参考]\n" + preamble.Trim();
        var nh = new ChatHistory(augmented);
        for (var i = 1; i < baseHistory.Count; i++)
            nh.Add(baseHistory[i]);
        return nh;
    }
}
