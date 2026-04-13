using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Chat;
using OfficeCopilot.Server.Services.DynamicTooling;

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
        var state = turn.State;
        var ctxConfig = turn.CtxConfig;
        var sessionManagerForStatus = turn.SessionManager;
        var planResult = turn.PlanResult;
        var clientType = sessionManagerForStatus.GetClientType(sessionId);
        var dynCfg = ctxConfig.DynamicTooling ?? new DynamicToolingConfig();

        await NotifyAgentStatusAsync(
            sessionManagerForStatus,
            sessionId,
            dynCfg.Enabled ? "正在准备动态工具…" : "正在准备工具…",
            ct).ConfigureAwait(false);
        _agentDebugStats.IncrementToolSelectionTotal();

        try
        {
            if (dynCfg.Enabled)
            {
                var catalog = ToolCatalogIndex.BuildFromAllowedTools(_runtime.ToolRegistry, clientType, sessionId);
                var skillCatalog = SkillCatalogIndex.BuildFromEnabledSkills(_skillService.GetAllSkills());
                var mergePlan = planResult != null;
                var bootstrap = SessionToolResolver.GetDynamicBootstrapTools(
                    _runtime.ToolRegistry,
                    clientType,
                    sessionId,
                    mergePlan,
                    dynCfg,
                    _skillService.GetAllSkills(),
                    _logger);
                var bootstrapNames = new List<string>();
                var nameSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in bootstrap)
                {
                    var n = t.Name;
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (!nameSeen.Add(n)) continue;
                    bootstrapNames.Add(n);
                }
                turn.DynamicToolingState = new DynamicToolingTurnState(dynCfg, catalog, skillCatalog, bootstrapNames)
                {
                    MergePlanIntoDynamicBootstrap = mergePlan,
                    ClientTypeForTools = clientType,
                    SessionIdForTools = sessionId
                };
                var trace = AgentTraceFormatter.BuildDynamicToolingBootstrapTrace(bootstrap.Count, catalog.Entries.Count);
                await NotifyAgentTraceAsync(
                    sessionManagerForStatus, sessionId, "toolSelection", trace.Title, trace.Detail, ct).ConfigureAwait(false);
                _agentDebugStats.RecordDynamicToolingBootstrap();
                _logger.LogInformation(
                    "[{SessionId}] Dynamic tooling: bootstrapCount={Boot} indexEntries={Idx}",
                    sessionId, bootstrap.Count, catalog.Entries.Count);
                var hasBootstrapDirectTools = bootstrap.Any(t =>
                    !string.IsNullOrEmpty(t.Name) && !DynamicToolingConstants.MetaFunctionNames.Contains(t.Name));
                FinalizeToolingPhaseTurn(
                    turn,
                    state,
                    clientType,
                    bootstrap,
                    selectedPairs: null,
                    restrictedSubset: true,
                    appendDynamicToolingInstruction: true,
                    appendBootstrapDirectToolsHint: hasBootstrapDirectTools);
            }
            else
            {
                turn.DynamicToolingState = null;
                IReadOnlyList<AITool>? selectedTools = SessionToolResolver.ResolveToolsByClientType(_runtime.ToolRegistry, null, clientType, sessionId);
                if (planResult != null && selectedTools != null)
                    selectedTools = SessionToolResolver.MergePlanTools(_runtime.ToolRegistry, selectedTools);
                var funcsCount = selectedTools?.Count ?? 0;
                _logger.LogInformation("[{SessionId}] Static tooling: full allow-list functionCount={Count}.", sessionId, funcsCount);
                await NotifyAgentTraceAsync(
                    sessionManagerForStatus, sessionId, "toolSelection", "全量允许工具",
                    $"本端可用 {funcsCount} 个函数。", ct).ConfigureAwait(false);
                FinalizeToolingPhaseTurn(
                    turn, state, clientType, selectedTools ?? Array.Empty<AITool>(), selectedPairs: null, restrictedSubset: false, appendDynamicToolingInstruction: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Tooling phase failed, using full allow-list.", sessionId);
            _agentDebugStats.RecordToolSelectionException();
            turn.DynamicToolingState = null;
            var selectedTools = SessionToolResolver.ResolveToolsByClientType(_runtime.ToolRegistry, null, clientType, sessionId);
            if (planResult != null && selectedTools != null)
                selectedTools = SessionToolResolver.MergePlanTools(_runtime.ToolRegistry, selectedTools);
            await NotifyAgentTraceAsync(
                sessionManagerForStatus, sessionId, "toolSelection", "工具阶段异常，已回退全量工具",
                ErrorMessageHelper.GetFriendlyMessage(ex), ct).ConfigureAwait(false);
            FinalizeToolingPhaseTurn(
                turn, state, clientType, selectedTools ?? Array.Empty<AITool>(), selectedPairs: null, restrictedSubset: false, appendDynamicToolingInstruction: false);
        }
    }

    private void FinalizeToolingPhaseTurn(
        StreamChatTurnContext turn,
        SessionState state,
        string? clientType,
        IReadOnlyList<AITool> toolsForAgent,
        IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs,
        bool restrictedSubset,
        bool appendDynamicToolingInstruction,
        bool appendBootstrapDirectToolsHint = false)
    {
        var sessionId = turn.SessionId;
        var ctxConfig = turn.CtxConfig;
        var pairsCount = selectedPairs?.Count ?? 0;
        var funcsCount = toolsForAgent.Count;
        _logger.LogInformation("[{SessionId}] Tooling: finalize pairsCount={PairsCount} toolsForAgentCount={FuncCount} restricted={Restricted} dynamicInstr={Dyn}.",
            sessionId, pairsCount, funcsCount, restrictedSubset, appendDynamicToolingInstruction);

        var maxOutputTokens = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
        turn.ExecSettings = new ChatOptions { MaxOutputTokens = maxOutputTokens };
        if (funcsCount > 0)
        {
            if (restrictedSubset)
                _logger.LogInformation("[{SessionId}] Tooling: MAF bound to {FunctionCount} functions (subset/bootstrap).", sessionId, funcsCount);
            else
                _logger.LogInformation("[{SessionId}] Tooling: MAF bound to {FunctionCount} functions (full allow-list).", sessionId, funcsCount);
        }
        else
            _logger.LogInformation("[{SessionId}] Tooling: MAF bound to zero tools (chat-only).", sessionId);

        turn.ToolsForAgentRound = toolsForAgent;
        turn.SelectedTools = funcsCount > 0 ? toolsForAgent : null;

        var activeModel = GetActiveModelEntry();
        turn.EnableSearchSuppressionSuffix = activeModel?.EnableSearch == true ? EnableSearchSuppressionInstruction : null;

        var id = GetClientTypeIdentitySuffix(clientType);
        if (appendDynamicToolingInstruction)
        {
            var dyn = DynamicToolingInstruction.Text;
            if (appendBootstrapDirectToolsHint)
                dyn += "\n\n" + DynamicToolingInstruction.BootstrapDirectToolsHint;
            id = string.IsNullOrEmpty(id) ? dyn : id + "\n\n" + dyn;
        }
        turn.IdentitySuffix = id;
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
