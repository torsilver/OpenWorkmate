# MAF 迁移基线

生成时间：Phase J 完成后更新；测试计数与包版本以本文「测试基线」「当前 NuGet 依赖」为准，变更代码后请本地复跑/核对。

## 测试基线

- 命令：`dotnet test backend.Tests/backend.Tests.csproj`
- **最近一次全绿计数**：583（`net10.0`，Unit + Integration，2026-04-23 校验）；后续以本地 `dotnet test backend.Tests/backend.Tests.csproj` 输出为准。

## 完全 MAF 迁移进度（摘要）

| 项 | 状态 |
|----|------|
| **SK NuGet 包** | **已删除** — `Microsoft.SemanticKernel` 与 `Microsoft.SemanticKernel.Connectors.OpenAI` 均已从 csproj 移除。 |
| **SK `using` 语句** | **零** — 后端代码中无任何 `using Microsoft.SemanticKernel*`。 |
| **`Kernel` 类型** | **零** — 全部移除。`ChatRuntimeAccessor` 不再持有 `Kernel`；`StreamChatTurnContext` 不再引用 `Kernel`。 |
| 主会话流式 | 仅 `MafMainSessionStreamRunner`（MEAI `IChatClient`）。 |
| 模型注册 | `OpenAIClient.GetChatClient(modelId)` 直接创建 `IChatClient`；按 entry id 存入字典。 |
| 嵌入 | `IEmbeddingGenerator<string, Embedding<float>>` (MEAI)，由 `OpenAIClient.GetEmbeddingClient` 直接实现。 |
| 辅助 LLM 调用 | 全部走 `IChatClient` + `List<ChatMessage>` + `ChatOptions`。 |
| 会话历史 | `SessionState.History` 为 `List<ChatMessage>` (MEAI)。 |
| 插件注册 | `[ToolFunction]` 自定义属性 + `ToolRegistry.RegisterPluginFromObject`（`AIFunctionFactory`）。 |
| 工具选择 | **动态工具**：`ToolCatalogIndex.BuildFromAllowedTools(..., wpsHostKind)`（关键词检索）+ `AgentToolingPlugin`（`search_available_tools` / `activate_tools`）；`SessionToolResolver` 按 `clientType`、会话与 **WPS `wpsHostKind`** 解析允许列表。 |
| 工具预筛选 | 主会话**无**独立向量工具索引；记忆/RAG 仍用 `IVectorStore`。 |
| 主会话工具面 | 首轮仅注入动态工具插件 + 少量引导工具；模型检索并 `activate` 后扩容本轮 `ChatOptions.Tools`。无 `ToolNeedGate` / 两阶段选型 LLM。 |
| MCP | `McpKernelPlugin.BuildMcpAIToolsAsync()` 产出 `IReadOnlyList<AITool>`，注册到 `ToolRegistry`。 |
| UserSkill (Prompt) | `AIFunctionFactory.Create` 包装为 `AIFunction`，通过 `IChatClient` 执行。 |
| 横切（安全/会话/tool 状态） | **MAF Function Calling Middleware** (`ToolInvocationMiddleware.Create`) → `SecurityPipeline`（HITL/白名单）→ SessionContext 注入 → `ToolStatusNotifier`（前端推送/审计）。通过 `agent.AsBuilder().Use(middleware).Build()` 注册到每个 `ChatClientAgent`，无需手动包装工具。 |
| SK 过滤器 | **已删除** — `FunctionInvocationGatewayFilter`、`SecurityFilter`、`SessionContextFilter`、`ToolStatusFilter` 文件已移除。 |
| `MafChatHistoryConverter` | **已删除** — 不再需要 SK↔MEAI 消息转换。 |

## 当前 NuGet 依赖（AI 相关）

| 包 | 版本 |
|----|------|
| `Microsoft.Agents.AI` | 1.0.0 |
| `Microsoft.Agents.AI.OpenAI` | 1.0.0 |
| `Microsoft.Agents.AI.Workflows` | 1.0.0 |
| `Microsoft.Extensions.AI` | 10.5.0 |

## DashScope / 推理流（迁移时需逐项对照）

- [ ] 兼容 OpenAI Chat Completions 请求体合并（`DashScopeChatRequestMerge`）
- [ ] 流式 SSE 与 `reasoning_content` 旁路（`DashScopeSseReasoningTapStream`）
- [ ] 非流式路径与工具调用一致性
- [ ] `DashScopeCallKindContext` / 后台调用分类

| 主会话阶段编排 | **MAF Workflows** (`ChatTurnWorkflow` → `FunctionExecutor` × 3 → `InProcessExecution.Default.RunAsync`)，替代旧 `ChatToolingRegistry`+`IChatTurnProcessCoordinator`（已删除）。内置 OpenTelemetry 可观测性。 |
| 上下文注入（记忆/知识库/计划/跨端） | **MAF `MessageAIContextProvider`**（`MemoryContextProvider`、`KnowledgeBaseContextProvider`、`PlanContextProvider`、`CrossAgentTaskContextProvider`），通过 `agent.AsBuilder().UseAIContextProviders(...)` 注册到主会话 `ChatClientAgent`。每轮按需创建，各 Provider 独立检索数据并返回额外 system 消息。原 `ChatService.StreamPhases` 中对 `state.History[0]` 的手工拼接已移除。 |
| MAF 宿主调试 HTTP 端点（DevUI / AG-UI） | **已移除**：不引用 `Microsoft.Agents.AI.DevUI` 与 `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`；不映射 `/devui`、`/agui` 及 OpenAI Responses/Conversations 调试端点；已删除 `RuntimeDelegatingChatClient`、`AgUiEventMapping.cs`。主会话仍仅经 WebSocket + `/api/*`。 |
| Compaction 内置摘要压缩 | **MAF `PipelineCompactionStrategy`**（`ToolResultCompactionStrategy` → `SummarizationCompactionStrategy` → `TruncationCompactionStrategy`），替代自定义 `TrySummarizeOldTurnsAsync` + `SummarizeOldTurnsCoreAsync`。通过 `CompactionProvider.CompactAsync` ad-hoc 应用。 |
| GroupChat 编排 | **MAF `AgentWorkflowBuilder.BuildSequential`** 替代手写 Host+Worker 两阶段循环。通过 `AgentResponseUpdateEvent.ExecutorId` 区分 Host/Worker 流式输出。 |
| Workflow Checkpoint | **MAF `CheckpointManager.CreateInMemory()`** 集成到 `ChatTurnWorkflow.RunAsync`。`HitlWorkflowContracts` + `WorkflowHitlBridge` 为 HITL→RequestPort 迁移预置基础设施。 |
