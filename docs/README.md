# `docs/` 文档索引

以下为仓库内设计、基线与测试类说明；**实现以代码为准**。增删大模块时请同步更新本索引。

## 架构与端到端

| 文档 | 用途 |
|------|------|
| [architecture-dimensions.md](./architecture-dimensions.md) | 仓库结构、运行时拓扑、后端分层、WS/API 概念图（Mermaid） |
| [用户请求-端到端时间线.md](./用户请求-端到端时间线.md) | 用户发起到 `stream_end` 的端到端流程（Chrome 为主） |

## Agent、上下文与提示词

| 文档 | 用途 |
|------|------|
| [Agent概念与项目实现对照.md](./Agent概念与项目实现对照.md) | 常见 Agent 概念与后端主会话路径、文件入口对照表 |
| [Agent优化.md](./Agent优化.md) | **已落地 / Backlog / 观测基线**（原 `Agent优化进展` + `Agent优化Backlog` 合并） |
| [项目优化建议.md](./项目优化建议.md) | 架构与工程层优化方向、与实现对齐 |
| [Token预算与上下文裁剪.md](./Token预算与上下文裁剪.md) | `ContextWindowConfig`、裁剪与重试、观测环境变量 |
| [动态上下文压缩.md](./动态上下文压缩.md) | MAF Compaction、启发式、`compact_conversation`、压缩边界 |
| [提示词清单.md](./提示词清单.md) | System 与各层提示词归档（与 `ConfigService` 等对照） |
| [Memory注入与search_memory分工.md](./Memory注入与search_memory分工.md) | 记忆注入与检索工具分工 |

## MAF 与迁移

| 文档 | 用途 |
|------|------|
| [maf-migration-baseline.md](./maf-migration-baseline.md) | MAF/MEAI 迁移状态、NuGet 基线、测试计数 |
| [maf-host-debug-removal.md](./maf-host-debug-removal.md) | 已移除的 MAF DevUI / AG-UI 等宿主调试端点说明 |

## 动态工具、插件与运行时

| 文档 | 用途 |
|------|------|
| [动态工具与技能选择实现说明.md](./动态工具与技能选择实现说明.md) | `ToolCatalogIndex`、bootstrap、`DynamicToolingTurnState` 与 MAF 主路径 |
| [模型运行时服务约定.md](./模型运行时服务约定.md) | 后端对工具调用客户端的义务：错误回传、动态工具同步、可观测性与安全 |
| [应用内AI插件列表.md](./应用内AI插件列表.md) | 内置/动态插件名与 `ClientTypeToolFilter` 可见性（含 WPS `wpsHostKind`） |

## 各端与测试

| 文档 | 用途 |
|------|------|
| [Chrome端手工测试计划.md](./Chrome端手工测试计划.md) | Chrome 扩展 + 本机服务的逐项手工回归 |
| [Chrome端手工测试-Playwright无法覆盖清单.md](./Chrome端手工测试-Playwright无法覆盖清单.md) | E2E 未覆盖、仍需手工的条目（与主计划配套） |
| [WPS与Office任务窗格手工测试计划.md](./WPS与Office任务窗格手工测试计划.md) | WPS / Office 任务窗格 + `CurrentDocument` RPC / 宿主守卫 / 动态工具手工回归（正文以 WPS 操作为主，Office 可对照） |
| [WPS插件调试指南.md](./WPS插件调试指南.md) | 官方加载项调试要点与 `wps-addin-new` 下 `wpsjs debug` 实践 |
| [对话测试清单.md](./对话测试清单.md) | 对话侧按功能复制的测试话术 |

## 其它

| 文档 | 用途 |
|------|------|
| [未完成功能与能力缺口.md](./未完成功能与能力缺口.md) | 代码扫描得到的边界与占位（非 `todo` 想法列表） |
| [开源AI借鉴落地.md](./开源AI借鉴落地.md) | 开源 Agent 模式与当前 MAF 栈的对照与优先级 |
| [多Agent五种组织模式选型指南.md](./多Agent五种组织模式选型指南.md) | 多 Agent 组织模式参考（外部文章归纳） |
| [streaming-asr-websocket-plan.md](./streaming-asr-websocket-plan.md) | 百炼实时 ASR（v1/inference）架构与配置 |
| [OpenXmlPowerTools引用说明.md](./OpenXmlPowerTools引用说明.md) | PPT 幻灯片复制为何自研、与 OpenXmlPowerTools NuGet 的关系 |

---

**已删除或合并**（需要时查 Git 历史）：

- `PROJECT_PLAN.md`：与当前 MAF 栈严重不符。
- `侧栏-页面抓取与Word导出-问题分析.md`：单次复现笔记。
- `Chrome端-Agent浏览器范围改造计划.md`：阶段已落地，见 `chrome-extension/` 与 `architecture-dimensions.md`。
- `多Agent配置-执行计划.md`：已落地。
- `WPS与Office右键菜单及临时输入框可行性.md`：可行性备忘。
- **`Agent优化进展.md` / `Agent优化Backlog.md`**：合并为 **[Agent优化.md](./Agent优化.md)**。
