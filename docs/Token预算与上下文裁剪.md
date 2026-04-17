# Token 预算与上下文裁剪

本文说明主会话中 **`ContextWindowConfig` / `SessionConfig` 与裁剪、重试** 的关系，便于与代码对照（见 `backend/Services/ContextManager.cs`、`backend/ChatService.StreamPhases.cs`）。

## 预算扣减顺序（概念）

| 概念 | 配置字段（约） | 说明 |
|------|----------------|------|
| 模型上下文上限 | `AiModelEntry.ContextLength` 或 `MaxContextTokens` | 单模型最大上下文 token |
| 预留 system | `ReservedSystemTokens` | 为 system / 注入等预留 |
| 预留工具 schema | `ReservedToolsTokens` | 为工具定义预留 |
| 预留输出 | `ReservedOutputTokens` | 为模型生成长度预留；亦用于 `ChatOptions.MaxOutputTokens` 夹取 |
| 历史可用预算 | `GetEffectiveMaxContextTokens() - Reserved* - ReservedOutput` | Part1 中 Compaction 的 `historyBudget` 等 |

`PassThroughContext == true` 时跳过按 token 的优化路径（仍可有轮数上限）。

## 裁剪与摘要

- **`ContextManager.TrimHistory`**：轮数上限（`MaxHistoryTurns`）+ token 预算；删除旧消息时使用 **`ConversationCompactBoundary.GetFirstRemovableChatIndex`**，保护带 `[compact_boundary:…]` 的摘要块。
- **MAF Compaction**（`SummarizationEnabled` 等）：工具结果折叠 → 摘要 → 截断；触发与 `SummarizationTriggerRatio`、`historyBudget` 相关。
- **Query-aware 启发式（实验，默认关）**：`CompactionQueryAwareHeuristicEnabled` 等为 true 时，在 Part1 于 MAF Compaction **之前**运行：若估算历史 token 已超过 `(有效上限 − ReservedOutput) × CompactionQueryAwareTokenPressureRatio`，则从可删区由旧到新删除与**当前用户句**词重叠为 0 的消息（上限 `CompactionQueryAwareMaxRemovalsPerTurn`），逻辑与 `CompactionRelevanceDiagnostics` 拆词一致。详见 `CompactionQueryAwareHeuristic`。
- **Context length 重试**：`AgentRunMiddleware` 在命中上下文长度类错误时调用 **`ContextManager.TrimHistoryForRetry`**（与 `ContextLengthRetryHelper` 委托同一实现），先按轮数再按半预算删，同样尊重压缩边界；单条 token 估算与主路径一致（**`EstimateMessageTokens`**，含多模态片段占位）。

## 观测

- 每用户轮生成 **`RoundId`**（`StreamChatTurnContext.RoundId`），并写入 **`SessionContext`**，日志与 OpenTelemetry Activity（如 `Maf.MainSession.Stream`）可带 `roundId`。
- 设置环境变量 **`OFFICECOPILOT_CONTEXT_SNAPSHOT_DIR`** 为可写目录时，送入 MAF 前可对 `HistoryToUse` 写脱敏快照 JSON（见 `ContextTurnSnapshot`）。

## 实验：摘要相关性诊断

设置 **`OFFICECOPILOT_COMPACTION_RELEVANCE_LOG=1`** 时，在 Compaction 前打一条「用户词与各消息重叠度」日志（不改变历史），用于评估 Query-aware 摘要，见 `CompactionRelevanceDiagnostics`。若同时开启 **`CompactionQueryAwareHeuristicEnabled`**，可对照日志观察启发式删除是否与「低重叠」一致。

## 观测基线（阶段一 checklist）

本地对比「压缩前 / 后」或 **Summarization 关 / 开** 时建议：

1. 设置 **`OFFICECOPILOT_COMPACTION_RELEVANCE_LOG=1`**（可选再加 **`OFFICECOPILOT_CONTEXT_SNAPSHOT_DIR`** 指向可写目录）。
2. 同一长会话下各跑一轮：**`SummarizationEnabled: false`**（仅轮数 + token 裁剪 + 可选启发式）与 **`SummarizationEnabled: true`**（再加 MAF Compaction）。
3. 在日志与前端 trace 中对照：`RoundId`、`CompactionRelevance` 行、**「历史压缩」** trace、是否出现 **context_length** 重试。

详见 [docs/Agent优化.md](Agent优化.md)。
