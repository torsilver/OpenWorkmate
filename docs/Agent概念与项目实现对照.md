# Agent 概念与项目实现对照

本文按 Agent 开发中的常见概念分支，对照本仓库**后端主会话路径**中的具体实现与主要文件位置。Chrome 扩展侧主要负责 WebSocket 消费与 UI；产品规范以 `chrome-extension/` 为准。

---

## 对照表

| 概念分支 | 在本项目里怎么落地 | 主要入口 / 文件 |
|----------|-------------------|-----------------|
| **上下文组装（Context assembly）** | 单轮共享 `StreamChatTurnContext`：用户消息先入 `SessionState.History`，再跑 MAF Workflow 三阶段（见下行）；主模型调用前用 **`SystemPromptBuilder.BuildHistoryForStreamingTurn`** 生成本轮**流式专用**历史：在**首条 system** 上追加客户端身份/页上下文（`IdentitySuffix`）、可选搜索抑制、固定 instruction 块等**不写入持久历史**的片段；MAF 侧再通过 `MessageAIContextProvider[]` 注入记忆、知识库、计划、跨端待办等**额外消息**。 | `SystemPromptBuilder.cs`、`StreamChatTurnContext.cs`、`ChatService.cs`（`StreamChatAsync`、`BuildContextProviders`）、`ChatTurnWorkflow.cs` |
| **编排流水线（Orchestration / phases）** | 用 MAF `Workflow` 串起：**ContextPrepPart1**（预算 + MAF Compaction）→ **ContextPrepPart2**（计划加载、日志）→ **ToolingPhase**（动态工具/工具表绑定）；`CheckpointManager` 支持检查点（注释里与上下文长度重试等恢复相关）。 | `ChatTurnWorkflow.cs`、`ChatService.StreamPhases.cs` |
| **历史裁剪 / Token 预算（Trimming）** | `ContextManager.TrimHistory`：先按 `Session.MaxHistoryTurns` 控制消息条数；再按估算 token 与 `ReservedOutputTokens` 等预算删旧消息；删除时通过 `ConversationCompactBoundary.GetFirstRemovableChatIndex` **保护摘要块**。`TrimHistory` 在 `ChatService` 追加用户消息后、准备上下文前调用。 | `ContextManager.cs`、`ChatService.cs`、`ConversationCompactBoundary.cs` |
| **摘要 / 压缩（Summarization / Compaction）** | Part1 中在配置允许且历史足够长时，调用 `CompactionProvider.CompactAsync`；策略为 **Pipeline**：工具结果折叠 → 摘要（`SummarizationCompactionStrategy`）→ 截断兜底；另有工具 `compact_conversation` 可走同类压缩。摘要正文通过 `ConversationCompactBoundary.BuildSummaryMessageBody` 带 `[compact_boundary:…]`；裁剪时**不删**该边界相关消息。 | `ChatService.StreamPhases.cs`（`BuildCompactionStrategy`）、`ChatService.cs`、`ConversationCompactBoundary.cs` |
| **上下文长度超限重试（Context-length retry）** | `ChatClientAgent` 中间件链上挂 `AgentRunMiddleware.CreateContextLengthRetry`：命中上下文长度类错误时按配置裁剪历史再试；`ContextManager.TrimHistoryForRetry` / `ContextLengthRetryHelper` 提供重试专用裁剪逻辑。 | `MafMainSessionStreamRunner.cs`、`ContextLengthRetryHelper.cs`、`ContextManager.cs` |
| **动态工具（Dynamic tool calling）** | 主会话**首轮**只给 **bootstrap** 子集；模型通过 `search_available_tools` / `activate_tools` 扩容；`DynamicToolingTurnScope`（AsyncLocal）保存本轮状态；`MafMainSessionStreamRunner` **外层循环**（最多 `maxOuterLoops`）在 expansion 时多跑 pass；同 pass 内 `ToolInvocationMiddleware.TryRefreshDynamicToolingToolsAfterActivate` 在 `activate_tools` 成功后**原地刷新** `ChatOptions.Tools` 绑定的列表；`DynamicToolingChatOptionsSyncChatClient` 从 scope 同步 Tools。检索排序在关键词打分之外可叠加 **`ToolCatalogSuccessBoost`**（进程内成功次数，见中间件语义成功路径）。 | `docs/动态工具与技能选择实现说明.md`、`ToolCatalogIndex.cs`、`ToolCatalogSuccessBoost.cs`、`DynamicToolingTurnContext.cs`、`MafMainSessionStreamRunner.cs`、`ToolInvocationMiddleware.cs`、`ChatService.StreamPhases.cs` |
| **工具注册表（Tool registry）** | 启动/重建运行时扫描内置插件 `[ToolFunction]` → `AITool`，动态 MCP 再 `RegisterPlugin`；`GetAllWithMetadata` 供过滤与动态工具索引。 | `ToolRegistry.cs`、`ChatService.cs`（`RebuildRuntimeAsync` 等） |
| **按客户端过滤工具（Tool allowlist / client routing）** | `ClientTypeToolFilter.IsAllowed`：按 `clientType` / `wpsHostKind` / `sessionId`（如 `scheduled:`）决定哪些 `(Plugin, Function)` 暴露给模型（例如 Chrome 不暴露 CurrentDocument；WPS 按宿主收窄等）。 | `ClientTypeToolFilter.cs` |
| **工具调用中间件（Tool invocation pipeline）** | `ToolInvocationMiddleware`：解析函数名 → **权限规则** `ToolPermissionRuleEvaluator` → **SecurityPipeline**（HITL/白名单）→ 注入 `SessionId` → `ToolStatus` 前后钩子 → 执行插件。高危 CLI/页面脚本等走 `HitlManager`。 | `ToolInvocationMiddleware.cs`、`SecurityPipeline.cs` |
| **检索增强 / 记忆（RAG / Memory）** | `MemoryContextProvider`：`IMemoryStoreService.SearchAsync` / `SearchSharedAsync`，按 `MemorySessionTopK` 等注入 **额外 system 消息**；失败或空结果写入 `ContextWarnings`。知识库为 `KnowledgeBaseContextProvider`。 | `MemoryContextProvider.cs`、`KnowledgeBaseContextProvider.cs` |
| **计划绑定（Plan grounding）** | `PlanContextProvider` 注入当前计划内容（长度受 `PlanContentMaxChars` 限制）；`RunStreamChatContextPhasePart2Async` 从 `PlanStore` 加载 `turn.PlanResult`。 | `PlanContextProvider.cs`、`ChatService.StreamPhases.cs` |
| **跨端任务（Cross-agent / handoff）** | `CrossAgentTaskContextProvider` 拉取待办列表注入 system；插件侧有 `CrossAgentTask` 等（与 `ICrossAgentTaskStore` 配合）。 | `CrossAgentTaskContextProvider.cs`、`ChatService.cs`（`BuildContextProviders`） |
| **流式输出（Streaming）** | MAF `RunStreamingAsync` 产出 `StreamItem`：`Program.cs` 里映射 WebSocket：`stream_chunk`、`tool_call_delta`、`reasoning_chunk` 等；`TimelineBlockStreamCoordinator` 维护块序号。 | `MafMainSessionStreamRunner.cs`、`Program.cs` |
| **推理分流（Reasoning vs answer）** | 百炼 SSE：`DashScopeReasoningSessionBridge` / `DashScopeReasoningContext` 产出 `StreamSegmentKind.Reasoning`；**仅展示**；主路径注释强调不参与业务分支。`ReasoningTagStreamParser` 在 HTTP 路径解析标签；Verifier 前对助手文用 `StripReasoningTags`。 | `MafMainSessionStreamRunner.cs`、`Program.cs`、`ChatService.cs` |
| **工具调用增量（Tool call deltas）** | `MafToolCallDeltaExtractor` 从 `AgentResponseUpdate` 提取参数增量，封装为 `StreamSegmentKind.ToolCallDelta`。 | `MafMainSessionStreamRunner.cs`（引用 `MafToolCallDeltaExtractor`） |
| **会话状态（Session state）** | `SessionState` 持久化对话历史；`SessionContext` / `SessionManager` 提供当前会话 id、客户端类型、WPS 宿主等；插件通过 `SessionContext.GetSessionId()` 取会话。 | `ChatService.cs`、`SessionManager`、各插件 |
| **轮次可观测（Round tracing）** | 每用户轮 `StreamChatTurnContext.RoundId`；`SessionContext.SetRoundId` / `GetRoundId` 供异步流关联；Compaction、MAF、回合结束等日志带同一 id；送入 MAF 前可 `ContextTurnSnapshot`（Debug 或环境变量落盘）。 | `StreamChatTurnContext.cs`、`SessionContext.cs`、`ChatService.cs`、`ContextTurnSnapshot.cs`、`MafMainSessionStreamRunner.cs` |
| **附件与多模态（Attachments）** | 附件存缓存，对话里只写 `attachment:guid`；`AttachmentRefChatMessageFactory` 根据是否支持视觉组装 `ChatMessage`（文本 + `DataContent`）。 | `ChatService.cs`（`StreamChatAsync` 开头） |
| **System 提示分层（Identity / 后缀）** | 持久 system 在 `state.History[0]`；每轮流式再拼 `IdentitySuffix`（客户端类型 + `ClientPageContextSuffixBuilder` 页签/宿主说明）、可选 `EnableSearchSuppressionSuffix`、以及固定 instruction 块——**仅本轮**注入 `HistoryToUse`（`SystemPromptBuilder.BuildHistoryForStreamingTurn`）。 | `SystemPromptBuilder.cs`、`ChatService.StreamPhases.cs`、`ClientPageContextSuffixBuilder.cs` |
| **轮次完成校验（Completion verifier）** | 动态工具模式下，流结束后可用内置 `BuiltinTurnCompletionVerifier` 根据用户消息、可见回复、search/activate 次数等判断是否「假完成」，必要时续跑（与 `TurnRoute` 等配合）。 | `ChatService.cs`（`ShouldInvokeBuiltinCompletionVerifier` 等）、`StreamChatTurnContext.cs` |
| **多 Agent / Group Chat** | 配置 `UseAgentGroupChatMainSession` 时走 `MafAgentGroupChatSessionRunner`（Host + 参与者），与主会话单 Agent 路径并列。 | `ChatService.cs`、`MafAgentGroupChatSessionRunner.cs` |
| **子 Agent（Subagent）** | 内置 `SubagentPlugin` 注册到 `ToolRegistry`，与主会话工具链一致。 | `ChatService.cs`、`backend/Plugins/SubagentPlugin.cs`（路径以仓库为准） |
| **MCP 工具** | 配置的 MCP 服务器启动后 `BuildMcpAIToolsAsync`，以 `MCP_` 插件名注册；`ClientTypeToolFilter` 将 `MCP_*` 视为通用插件一类参与过滤。 | `ChatService.cs`、`McpKernelPlugin.cs` |

---

## 结构小结

- **「上下文组装」** 拆成：**持久历史**（`SessionState.History` + `TrimHistory`）→ **MAF Compaction 摘要链**（Part1）→ **本轮流式副本**（`SystemPromptBuilder.BuildHistoryForStreamingTurn`，只改副本）→ **MessageAIContextProvider 注入**（记忆/知识库/计划/跨端任务）。
- **「剪裁」** 同时存在：**轮数/token 级删除**（`ContextManager`）、**MAF 压缩管线**（摘要+截断）、**压缩边界保护**（`ConversationCompactBoundary`）、以及 **context_length 重试裁剪**。
- **「动态工具调用」** 是 **bootstrap + 检索/激活 + AsyncLocal 状态 + 中间件原地刷新 Tools + 外层循环**；详见 `docs/动态工具与技能选择实现说明.md` 与 `MafMainSessionStreamRunner` / `ToolInvocationMiddleware`。

---

## 相关文档

- `docs/动态工具与技能选择实现说明.md`
- `docs/提示词清单.md`（含压缩边界、分层说明等）
- `docs/Token预算与上下文裁剪.md`
- `docs/Agent优化进展.md`（路线图落地清单，与本表互补）
