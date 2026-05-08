# Agent 优化（进展与 Backlog）

本文与 [项目优化建议.md](项目优化建议.md) 配套：**已落地**与**中长期排期**分节维护，避免两处各写一份。

---

## 已落地

| 项 | 说明 |
|----|------|
| System 拼装集中化 | `SystemPromptBuilder`：`BuildHistoryForStreamingTurn`、`GetClientTypeIdentitySuffix`、固定指令常量；单元测试见 `backend.Tests/Unit/SystemPromptBuilderTests.cs` |
| 轮次关联 | `StreamChatTurnContext.RoundId`、`SessionContext.SetRoundId` / `GetRoundId`；Compaction、MAF 流、回合结束等日志带 `RoundId`；`Maf.MainSession.Stream` Activity 含 `roundId` |
| Context 只读快照 | `ContextTurnSnapshot.TryLogAndOptionalFile`：默认 Debug 日志；环境变量 `OpenWorkmate_CONTEXT_SNAPSHOT_DIR` 时落盘 JSON（见类注释：与持久历史、本轮 `HistoryToUse` 对照） |
| Token 预算文档 | [Token预算与上下文裁剪.md](Token预算与上下文裁剪.md)；`ContextWindowConfig` 注释引用 |
| Memory 分工文档 | [Memory注入与search_memory分工.md](Memory注入与search_memory分工.md) |
| 重试裁剪与主路径一致 | `ContextLengthRetryHelper` 委托 `ContextManager.TrimHistoryForRetry`；`TrimHistoryForRetry` 的 token 与主路径一致（`EstimateMessageTokens`，含多模态片段占位） |
| 动态工具排序增强 | `ToolCatalogSuccessBoost` 进程内成功计数；`ToolCatalogIndex.Search` 可选加分；单元测试见 `ToolCatalogIndexTests` / `ToolCatalogSuccessBoostTests` |
| Compaction 实验诊断 | `CompactionRelevanceDiagnostics`：`OpenWorkmate_COMPACTION_RELEVANCE_LOG=1` 时打重叠度日志 |
| Query-aware 启发式（实验） | `CompactionQueryAwareHeuristic` + `ContextWindowConfig.CompactionQueryAware*`；Part1 在 MAF 前可选删低重叠旧消息；单元测试 `CompactionQueryAwareHeuristicTests` |
| 动态上下文压缩说明 | [动态上下文压缩.md](动态上下文压缩.md) 与实现对齐 |

---

## 中长期 Backlog

以下来自《项目优化建议》与路线图，**未在当期实现**，仅作排期参考。

| 方向 | 说明 |
|------|------|
| Tool Planning | 先输出结构化步骤再执行，减少动态工具 outer loop；需与 Verifier、协议对齐 |
| 显式 Agent 状态机 | IDLE → PLANNING → TOOL_CALLING → …；需与 MAF `Workflow` 协调，避免双轨 |
| Verifier 深度 Self-Reflection | 多轮自检可能增加延迟；宜可选档位 |
| Timeline Debug UI | 依赖后端 `roundId` 与结构化事件稳定后再做 Chrome 侧 |
| 广谱 Tool Cache | 仅在对失效策略有把握的场景（如只读 catalog）；文档类默认不缓存 |
| Query-aware Compaction（Embedding） | 已有 **重叠度诊断日志** + 可选 **词重叠启发式删条**（`CompactionQueryAwareHeuristic`，默认关）；Embedding 排序/保留仍待数据验证 |

---

## 观测基线（阶段一）

在改 Compaction 策略或调预算前，建议先做一次可复现对照：

1. 设置 **`OpenWorkmate_COMPACTION_RELEVANCE_LOG=1`**；可选设置 **`OpenWorkmate_CONTEXT_SNAPSHOT_DIR`** 为可写目录。
2. 用同一「长会话」各跑一轮：**`SummarizationEnabled: false`**（仅轮数 + token 裁剪 + 可选启发式）与 **`SummarizationEnabled: true`**（再加 MAF Compaction）。
3. 记录：`RoundId`、日志中的 `CompactionRelevance` 行、前端 **context** trace（「历史压缩」等）、是否触发 **context_length** 重试。

更细的字段说明见 [Token预算与上下文裁剪.md](Token预算与上下文裁剪.md)（文中「观测基线」一节）。

---

## 上下文体系统一（阶段三）

当前以 **只读快照 + 文档** 为主：先通过 `ContextTurnSnapshot` 与 `RoundId` 建立「本轮送入模型」与「持久历史」的对应关系；是否抽象为独立 **Context 块 / Compose** 接口见 [项目优化建议 §一](项目优化建议.md)，待专项迭代。

---

## 建议下一专项（阶段四）

在 **`roundId` 与结构化事件** 稳定后，优先评估 **Timeline Debug UI（Chrome）**；或单独排 **Tool Planning**（与 Verifier、动态工具协议对齐）。详见上文 Backlog 表。
