# MAF 迁移基线

生成时间：Phase F 完成后更新。

## 测试基线

- 命令：`dotnet test backend.Tests/backend.Tests.csproj`
- **最近一次全绿计数**：318（`net10.0`，Unit）。

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
| 工具选择 | `ToolSelectionService` 从 `ToolRegistry` 枚举 `(PluginName, FunctionName)` 对。 |
| 工具索引 | `IToolIndexService.BuildIndexAsync(ToolRegistry)` / `SyncUserToolIndexAsync(ToolRegistry)`。 |
| MCP | `McpKernelPlugin.BuildMcpAIToolsAsync()` 产出 `IReadOnlyList<AITool>`，注册到 `ToolRegistry`。 |
| UserSkill (Prompt) | `AIFunctionFactory.Create` 包装为 `AIFunction`，通过 `IChatClient` 执行。 |
| 横切（安全/会话/tool 状态） | `ToolInvocationPipelineFunction` → `SecurityPipeline`（HITL/白名单）→ SessionContext 注入 → `ToolStatusNotifier`（前端推送/审计）。所有工具在 `ToolRegistry.WrapAllTools()` 时自动包装。 |
| SK 过滤器 | **已删除** — `FunctionInvocationGatewayFilter`、`SecurityFilter`、`SessionContextFilter`、`ToolStatusFilter` 文件已移除。 |
| `MafChatHistoryConverter` | **已删除** — 不再需要 SK↔MEAI 消息转换。 |

## 当前 NuGet 依赖（AI 相关）

| 包 | 版本 |
|----|------|
| `Microsoft.Agents.AI` | 1.0.0 |
| `Microsoft.Agents.AI.OpenAI` | 1.0.0 |
| `Microsoft.Agents.AI.Workflows` | 1.0.0 |
| `Microsoft.Extensions.AI` | 10.4.0 |

## DashScope / 推理流（迁移时需逐项对照）

- [ ] 兼容 OpenAI Chat Completions 请求体合并（`DashScopeChatRequestMerge`）
- [ ] 流式 SSE 与 `reasoning_content` 旁路（`DashScopeSseReasoningTapStream`）
- [ ] 非流式路径与工具调用一致性
- [ ] `DashScopeCallKindContext` / 后台调用分类

## 后续待办

1. **MAF Workflows 进阶**（可选）：将 `ChatToolingRegistry` 的 context/tooling 编排升级为 `WorkflowBuilder + InProcessExecution`。
