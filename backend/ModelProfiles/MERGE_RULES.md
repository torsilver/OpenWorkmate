# taskly-overlay 与 vendor 摘录合并规则

1. **基底**：`vendor/model_prices_excerpt.json` 为单个 JSON 对象，顶层键为 LiteLLM 模型 id（如 `moonshot/kimi-k2.6`），值为 LiteLLM 原字段（`max_input_tokens`、`supports_vision` 等）。
2. **覆盖**：`taskly-overlay.json` 顶层键与 profileKey 对齐；值为 **Taskly 扩展** 字段（与 LiteLLM 字段并存，同名时扩展优先用于 Taskly 语义，勿覆盖 LiteLLM 定价字段名除非有意为之）。
3. **运行时**：`ModelProfileRegistry` 将两文件按 profileKey 合并为 `MergedModelProfile`；`AiModelEntry.modelProfileKey` 显式指向 profileKey；未配置时不参与合并。
4. **私有扩展字段**（camelCase JSON）：
   - `requiresReasoningEchoWithTools`：上游在 thinking 开启且多轮工具时可能要求回传 `reasoning_content`（诊断：缺缓冲时打 Warning；**不再作为启用 HTTP echo 的唯一开关**）。
   - `suppressUpstreamThinkingWithTools`：当检测到 assistant `tool_calls` 且缺少 `reasoning_content` 时，在出站 `POST .../chat/completions` 请求体顶层写入 `thinking: false`，以降低 400 风险（权衡：工具后续轮可能无 thinking）。Kimi 推荐依赖 `OpenAiReasoningEchoHandler` + `useThinkingKeepAll`，一般无需再 suppress。
   - `useThinkingKeepAll`：对 Kimi 等合并顶层 `thinking: { type: enabled, keep: all }`，与历史 `reasoning_content` 配合（见 Moonshot 文档）。
   - `disableReasoningHttpEcho`：为 true 时跳过 `OpenAiReasoningEchoHandler`（不 patch messages、不解析 SSE reasoning）；用于已知冲突的上游。
   - **默认 HTTP echo**：非百炼直连的 OpenAI 兼容 `chat/completions` 流式主会话由 `OpenAiReasoningEchoHandler` 自动解析 `reasoning_content` 并在工具延续轮注入；百炼 URL 仍由 `DashScopeOpenAiCompatHandler` 处理，避免双 tap。
   - `recommendedEnableThinking` / `recommendedThinkingBudget`：仅文档与日志建议，**不**自动写回 `user-config`。
   - `notes`：人读说明。
