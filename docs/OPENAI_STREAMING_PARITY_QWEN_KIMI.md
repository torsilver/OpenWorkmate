# 百炼 Qwen3.x 与 Kimi K2.6 流式「种类」对照（OpenAI 兼容）

权威文档（请在升级模型行为时复核）：

- 百炼流式与 `chat.completion.chunk`：[阿里云 Model Studio 流式输出](https://help.aliyun.com/zh/model-studio/stream)、[OpenAI Chat 兼容](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions)
- Kimi K2.6：[Kimi K2.6 快速开始](https://platform.kimi.ai/docs/guide/kimi-k2-6-quickstart)
- MEAI 对非标准 delta 的讨论：[Agent Framework #5327](https://github.com/microsoft/agent-framework/issues/5327)

## 上游 chunk 常见字段（SSE `data:` JSON）

| 种类 | 典型 JSON 路径 | Taskly `StreamSegmentKind` | 到 UI 的 WS `type` |
|------|----------------|---------------------------|-------------------|
| 正文增量 | `choices[0].delta.content` | `Normal` | `stream_chunk`（经 `ReasoningTagStreamParser`） |
| 推理增量 | `choices[0].delta.reasoning_content`（及少数别名） | `Reasoning` | `reasoning_chunk` |
| 工具调用流 | `choices[0].delta.tool_calls` | `ToolCallDelta` | `tool_call_delta` |
| 角色 | `choices[0].delta.role` | `StreamRole` | `stream_role` |
| 结束原因 | `choices[0].finish_reason` | `StreamFinish` | `stream_finish` |
| Token 用量 | 顶层 `usage`（常配合 `stream_options.include_usage`） | `StreamUsage` | `stream_usage` |
| 响应元数据 | `id` / `model` / `created` 等 | `StreamMeta` | `stream_meta` |

## 实现要点（代码侧）

- **百炼**：请求合并 + SSE 旁路仍在 `DashScopeOpenAiCompatHandler`；旁路同时解析 `usage` 并入队 `OpenAiStreamUsageSessionBridge`。
- **非百炼（含 Kimi、OpenAI 等）**：`OpenAiReasoningSseTapDelegatingHandler` 对 `POST …/chat/completions` 流式响应挂同一套 SSE 解析（`reasoning_content` + `usage`），避免 Kimi 直连时思考仅依赖 MEAI 映射导致丢失。
- **MEAI**：从 `ChatResponseUpdate` 抽取 `FinishReason`、`Role`、首包 `ResponseId`/`ModelId` 等，映射为 `StreamFinish` / `StreamRole` / `StreamMeta`（与 SSE 互补，去重在 `MafMainSessionStreamRunner` 内用局部状态完成）。
