# `docs/` 文档索引

以下为仓库内设计、基线与测试类说明；**实现以代码为准**。若增删大模块，请同步更新对应条目。

| 文档 | 用途 |
|------|------|
| [architecture-dimensions.md](./architecture-dimensions.md) | 仓库结构、运行时拓扑、后端分层、WS/API 概念图（Mermaid） |
| [maf-migration-baseline.md](./maf-migration-baseline.md) | MAF/MEAI 迁移状态、NuGet 基线、测试计数 |
| [maf-host-debug-removal.md](./maf-host-debug-removal.md) | 已移除的 MAF DevUI / AG-UI 等宿主调试端点说明 |
| [应用内AI插件列表.md](./应用内AI插件列表.md) | 内置/动态插件名与 `ClientTypeToolFilter` 可见性 |
| [提示词清单.md](./提示词清单.md) | System 与各层提示词归档（与 `ConfigService` 等对照） |
| [模型运行时服务约定.md](./模型运行时服务约定.md) | 后端对「工具调用客户端」的义务：错误回传、动态工具同步、可观测性与安全边界 |
| [开源AI借鉴落地.md](./开源AI借鉴落地.md) | 开源 Agent 模式与当前 MAF 栈的对照与优先级 |
| [未完成功能与能力缺口.md](./未完成功能与能力缺口.md) | 代码扫描得到的边界与占位（非 `todo` 想法列表） |
| [Chrome端手工测试计划.md](./Chrome端手工测试计划.md) | Chrome 扩展 + 本机服务的逐项手工回归 |
| [对话测试清单.md](./对话测试清单.md) | 对话侧按功能复制的测试话术 |
| [streaming-asr-websocket-plan.md](./streaming-asr-websocket-plan.md) | 百炼实时 ASR（v1/inference）架构与配置 |
| [OpenXmlPowerTools引用说明.md](./OpenXmlPowerTools引用说明.md) | PPT 幻灯片复制为何自研、与 OpenXmlPowerTools NuGet 的关系 |
| [WPS与Office右键菜单及临时输入框可行性.md](./WPS与Office右键菜单及临时输入框可行性.md) | 右键/临时输入框类产品设想的可行性备忘 |

**已删除**：`PROJECT_PLAN.md`（内容与当前 MAF 栈严重不符，历史里程碑以 `maf-migration-baseline.md` 与 Git 历史为准。）
