# Agent 优化进展（相对路线图）

本文记录《基于项目优化建议的执行计划》中**已落地**项，便于与代码同步。

## 已落地

| 项 | 说明 |
|----|------|
| System 拼装集中化 | `SystemPromptBuilder`：`BuildHistoryForStreamingTurn`、`GetClientTypeIdentitySuffix`、固定指令常量；单元测试见 `backend.Tests/Unit/SystemPromptBuilderTests.cs` |
| 轮次关联 | `StreamChatTurnContext.RoundId`、`SessionContext.SetRoundId` / `GetRoundId`；Compaction、MAF 流、回合结束等日志带 `RoundId`；`Maf.MainSession.Stream` Activity 含 `roundId` |
| Context 只读快照 | `ContextTurnSnapshot.TryLogAndOptionalFile`：默认 Debug 日志；环境变量 `OFFICECOPILOT_CONTEXT_SNAPSHOT_DIR` 时落盘 JSON |
| Token 预算文档 | [docs/Token预算与上下文裁剪.md](Token预算与上下文裁剪.md)；`ContextWindowConfig` 注释引用 |
| Memory 分工文档 | [docs/Memory注入与search_memory分工.md](Memory注入与search_memory分工.md) |
| 重试裁剪与主路径一致 | `ContextLengthRetryHelper` 委托 `ContextManager.TrimHistoryForRetry`（含压缩边界） |
| 动态工具排序增强 | `ToolCatalogSuccessBoost` 进程内成功计数；`ToolCatalogIndex.Search` 可选加分；单元测试见 `ToolCatalogIndexTests` / `ToolCatalogSuccessBoostTests` |
| Compaction 实验诊断 | `CompactionRelevanceDiagnostics`：`OFFICECOPILOT_COMPACTION_RELEVANCE_LOG=1` 时打重叠度日志 |

## 未纳入本期（见 Backlog）

[docs/Agent优化Backlog.md](Agent优化Backlog.md)
