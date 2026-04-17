# Agent 优化 Backlog（中长期）

以下条目来自《项目优化建议》与路线图，**未在当期实现**，仅作排期参考。

| 方向 | 说明 |
|------|------|
| Tool Planning | 先输出结构化步骤再执行，减少动态工具 outer loop；需与 Verifier、协议对齐 |
| 显式 Agent 状态机 | IDLE → PLANNING → TOOL_CALLING → …；需与 MAF `Workflow` 协调，避免双轨 |
| Verifier 深度 Self-Reflection | 多轮自检可能增加延迟；宜可选档位 |
| Timeline Debug UI | 依赖后端 `roundId` 与结构化事件稳定后再做 Chrome 侧 |
| 广谱 Tool Cache | 仅在对失效策略有把握的场景（如只读 catalog）；文档类默认不缓存 |
| Query-aware Compaction（Embedding） | 当前仅有环境变量触发的**重叠度诊断日志**；完整策略待数据验证 |

详见 [docs/项目优化建议.md](项目优化建议.md)、[docs/Agent优化进展.md](Agent优化进展.md)。
