# 办公自动化 AI 助手 (Office Copilot) 开发计划

## 🎯 项目概述
本项目旨在开发一款以 Chrome 插件为前端入口，基于本地 C# 服务驱动的智能办公自动化助手。它能够理解自然语言，自动操作本地 Office 软件（Excel, Word），执行系统命令，并具备动态生成前端图表/报表的功能。

### 核心技术栈
- **前端 (UI/交互)**: Chrome 插件 (Manifest V3), HTML/CSS/JavaScript, 侧边栏 (Side Panel)。
- **后端 (业务逻辑与 Agent)**: C# / .NET 10, ASP.NET Core (用于承载 WebSocket 服务)。
- **AI 编排框架**: Microsoft Semantic Kernel (SK)。
- **Office 操控技术**: 优先使用 OpenXML SDK (`DocumentFormat.OpenXml`) 进行文件读写；需要操控 Office UI 时回退到 COM Interop (`NetOffice`)。
- **系统操控技术**: `System.Diagnostics.Process` 执行 CLI 命令。
- **通信协议**: WebSocket (实时双向通信)。
- **配置管理**: `appsettings.json` + 用户配置文件 (`user-config.json`)。

### 架构要点
```
┌─────────────────────┐      WebSocket (ws://localhost:8765/ws)
│  Chrome Extension    │  ◄────────────────────────────────────►  ┌──────────────────────┐
│  (Manifest V3)       │     Origin 校验 + Token 认证              │  C# 本地服务 (.NET 10) │
│                      │                                           │                      │
│  ┌───────────────┐   │                                           │  ┌────────────────┐  │
│  │  Side Panel    │───┤  WebSocket 连接由 Side Panel 持有          │  │ Semantic Kernel │  │
│  │  (Chat UI)     │   │  Service Worker 仅做事件调度               │  │  ┌──────────┐  │  │
│  └───────────────┘   │                                           │  │  │ Plugins  │  │  │
│  ┌───────────────┐   │                                           │  │  │ - CLI    │  │  │
│  │ background.js  │───┤                                           │  │  │ - Excel  │  │  │
│  │ (Service Worker│   │                                           │  │  │ - Word   │  │  │
│  │  事件调度)     │   │                                           │  │  └──────────┘  │  │
│  └───────────────┘   │                                           │  └────────────────┘  │
│  ┌───────────────┐   │                                           │  ┌────────────────┐  │
│  │ Canvas 展示板  │   │  ◄── render_html 消息                     │  │  SK Filters    │  │
│  │ (iframe.srcdoc)│   │                                           │  │  (安全拦截)     │  │
│  └───────────────┘   │                                           │  └────────────────┘  │
└─────────────────────┘                                           └──────────────────────┘
```

---

## 🗓️ 开发里程碑与详细任务

### 阶段一：打通奇经八脉（基建与通信连接）
**目标**：建立可靠的前后端双向通信通道，包含基本安全校验，并实现多页面身份标识。

- [x] **1.1 C# 本地服务端搭建**
  - 创建基于 .NET 10 的 ASP.NET Core 最小化 Web API 项目。
  - 引入并配置 WebSocket 监听（监听 `ws://localhost:8765/ws`）。
  - 实现基于 `SessionID` 的连接管理器 (`ConcurrentDictionary<string, WebSocket>`)。
  - 创建 `appsettings.json` 配置文件，集中管理端口号、模型 API 地址等参数。
- [x] **1.2 WebSocket 安全基线**
  - 握手阶段校验 `Origin` 头，仅允许来自 `chrome-extension://<extension-id>` 的连接。
  - 实现简单的 Token 认证：插件首次连接时携带预共享 Token，服务端验证后建立连接。
  - 拒绝非法来源连接并记录日志（防止恶意网页通过 localhost 发起攻击）。
- [x] **1.3 Chrome 插件骨架开发**
  - 编写 `manifest.json` (配置 `side_panel`, `permissions` 等)。
  - 开发基础的聊天界面 (Chat UI: 消息列表、输入框、发送按钮)。
  - **WebSocket 连接由 Side Panel 页面持有**（而非 Service Worker），避免 MV3 Service Worker 空闲 30 秒后被终止导致连接断开。
  - `background.js` (Service Worker) 仅负责事件调度（如监听插件图标点击、管理 Side Panel 生命周期），不维持长连接。
  - 生成唯一 `SessionID` (基于 TabID 或 UUID) 并在连接时发送给服务端。
- [x] **1.4 前后端联调测试**
  - 插件通过 WebSocket 连接本地服务。
  - 实现简单的 Echo 测试：前端发送消息，C# 原样或加前缀后返回，前端渲染在聊天框内。
  - 验证 Origin 校验与 Token 认证流程正确拦截非法连接。

### 阶段二：注入灵魂（接入 Semantic Kernel 与 Agent 隔离）
**目标**：引入大模型，实现流式 AI 对话，确保多开页面的上下文互不干扰。

- [x] **2.1 引入 Semantic Kernel**
  - NuGet 安装 `Microsoft.SemanticKernel` 相关核心包。
  - 在 `appsettings.json` 中配置大语言模型 API 密钥与端点 (支持 OpenAI, DeepSeek 或其他兼容接口)。
  - 敏感配置（API Key）通过环境变量或 `user-secrets` 管理，不硬编码在代码中。
- [x] **2.2 多 Session 会话隔离**
  - 在 C# 服务中引入对话历史管理器：`ConcurrentDictionary<string, ChatHistory>`。
  - 收到前端消息时，根据 `SessionID` 获取或新建 `ChatHistory`。
  - 定义系统提示词 (System Prompt)，明确助手的角色、能力和职责边界。
  - **对话历史长度管理**：实现滑动窗口策略，当对话轮次超过阈值（如 50 轮）时，对早期对话进行摘要压缩，防止超出模型 Token 上限。
  - Session 超时回收：空闲超过 30 分钟的 Session 自动清理，释放内存。
- [x] **2.3 流式对话闭环联调**
  - 将用户消息追加到对应的 `ChatHistory`。
  - 使用 SK 的 **`GetStreamingChatMessageContentsAsync`** 生成流式回复。
  - 通过 WebSocket **逐块推送** Token 到前端，实现打字机效果（Typewriter Effect），显著提升交互体验。
  - 定义消息协议格式：
    ```json
    {"type": "stream_start", "sessionId": "xxx"}
    {"type": "stream_chunk", "content": "你好"}
    {"type": "stream_end"}
    {"type": "error", "message": "..."}
    ```

### 阶段三：长出实体的"手脚"（Office 操控与 CLI 插件）
**目标**：编写核心的 SK Plugins，赋予大模型读写本地文件和执行命令的能力。

- [x] **3.1 开发 CLI 命令行插件 (CLI Plugin)**
  - 创建 C# 类，编写 `[KernelFunction]` 标注的工具方法。
  - 使用 `Process` 类执行隐藏控制台的命令，捕获标准输出和标准错误。
  - 设置命令执行超时（默认 30 秒），防止进程挂起。
  - 限制输出长度，避免巨量输出撑爆内存。
- [x] **3.2 开发 Excel 插件 (Excel Plugin)**
  - **优先使用 OpenXML SDK** (`DocumentFormat.OpenXml`) 读写 `.xlsx` 文件（无需安装 Office，性能好，与 AOT 兼容）。
  - 编写原子能力工具：
    - `ReadExcelRange`: 读取指定文件指定单元格/区域的数据。
    - `WriteExcelData`: 向指定文件指定区域写入数据。
    - `CreateExcelReport`: 根据 JSON 数据生成新的 Excel 文件。
    - `ListSheets`: 列出工作簿中所有 Sheet 名称。
  - 对于需要操控 Excel UI 的场景（如刷新数据透视表），回退使用 COM Interop (`NetOffice.ExcelApi`)，确保 `Visible = false` 并正确释放 COM 对象。
- [x] **3.3 开发 Word 插件 (Word Plugin)**
  - 使用 OpenXML SDK 读写 `.docx` 文件。
  - 编写原子能力工具：
    - `ReadWordContent`: 提取文档的文本内容（支持按段落/表格提取）。
    - `WriteWordDocument`: 根据结构化数据或模板生成 Word 文档。
    - `FindAndReplace`: 在文档中查找并替换文本。
- [x] **3.4 注册并开启大模型自动调用 (Auto-Invoke)**
  - 将 CLI、Excel 和 Word 插件注册到 `Kernel` 中。
  - 配置 `ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions`。
  - 测试场景：
    - "帮我查一下 D:\data\sales.xlsx 的 A1 单元格内容"
    - "在 D:\output 下创建一个月度汇总 Excel"
    - "读取 D:\docs\report.docx 的第二段内容"

### 阶段四：安全加固与动态展示板 (Canvas)
**目标**：防止 AI 执行危险操作，支持人机交互确认，并实现复杂排版的动态渲染。

- [x] **4.1 安全拦截器与白名单机制 (SK Filter)**
  - 在 C# 中实现 `IFunctionInvocationFilter` 接口。
  - 针对 `CLI Plugin`：实现命令白名单校验（如仅允许 `dir`, `type`, `echo`，禁止 `del`, `format`, `rd` 等危险命令）。
  - 针对文件操作类插件：限制可访问的目录范围（可配置白名单路径）。
  - **HITL 已实现**：若触发拦截，通过 WebSocket 推送 `confirm_request`（含 `id`、`content` 操作描述）到前端；前端展示确认框，用户点击「允许」/「拒绝」后发送 `confirm_response`（`id`、`allowed`）；后端 60 秒内未收到则视为拒绝，根据结果继续执行工具或返回「用户拒绝/超时」。
- [x] **4.2 前端"展示板"架构设计**
  - 在 Chrome 插件 UI 中增加展示区 (sandboxed iframe)。
  - 定义前后端特殊消息协议：
    ```json
    {"type": "render_html", "content": "<html>...</html>", "title": "月度销售图表"}
    ```
  - 展示板支持多 Tab 切换，保留历史渲染结果。
- [x] **4.3 动态渲染与刷新**
  - 提示词中指导模型在需要展示报表或图表时，生成带图表库 (如 ECharts, Chart.js) 的完整 HTML 代码。
  - 插件接收到 HTML 代码后，通过 `iframe.srcdoc` 动态挂载并渲染。
  - iframe 设置 `sandbox` 属性，限制脚本权限（禁止访问父页面 DOM）。

### 阶段五：优化、打包与分发
**目标**：提升软件稳定性，并让普通用户可以轻松安装使用。

- [x] **5.1 健壮性与异常处理**
  - 完善 C# 端 WebSocket 的断线重连机制（前端自动重连 + 指数退避策略）。
  - 全局异常捕获与日志记录（使用 `Serilog` 或内置 `ILogger`，输出到文件）。
  - COM Interop 场景的进程残留处理 (`Marshal.ReleaseComObject` + `GC.Collect` + 兜底进程检测)。
- [x] **5.2 一键打包与分发**
  - 使用 .NET 10 **`PublishSingleFile` + `SelfContained`** 模式打包成独立 `.exe`（兼容 COM Interop 和反射，比 AOT 更稳妥）。
  - 若未来移除 COM 依赖（全面使用 OpenXML），可切换为 AOT 编译以获得更小体积和更快启动。
  - 打包 Chrome 插件 (`.zip` 格式，配合开发者模式加载；或发布到 Chrome Web Store)。
  - 编写面向普通用户的快速安装使用文档。
- [x] **5.3 自动启动与系统托盘**
  - C# 服务支持以系统托盘应用运行（最小化到托盘，开机自启可选）。
  - 托盘菜单提供：查看日志、打开配置、退出等操作。

### 阶段六：平台化与开放生态 (MCP & Skills) (新规划)
**目标**：将工具升级为平台，提供可视化的模型配置、自定义业务流 (Skills) 以及支持接入第三方标准 MCP 服务。

- [x] **6.1 配置管理层与 REST API**
  - 在 C# 后端增加 SQLite (使用 EF Core 或 Dapper) 或 JSON 本地文件存储。
  - 提供标准的 REST API (如 `/api/config`) 用于管理大模型密钥、系统设置等，取代硬编码的 `appsettings.json`。
  - 重构 `ChatService`，使其能够在 API 密钥更新时热重载 (Hot-reload) Kernel，而不是作为单例固定启动。
- [x] **6.2 前端可视化控制台 (Settings UI)**
  - 在 Chrome 插件中增加独立的 `options.html` (设置页) 或在 Side Panel 中增加完整的设置抽屉。
  - 实现基于 Vue/React 或原生 JS 的表单，绑定后端的 REST API，让用户可以所见即所得地切换 DeepSeek、Kimi 等不同模型。
- [x] **6.3 动态技能系统 (Prompt-based Skills)**
  - 后端提供针对 `Skills` 的 CRUD 接口。
  - 允许用户在前端写一段 Prompt 描述业务逻辑（例如：“帮我生成周报：读取 {{日志}} 写入 {{周报}}”）。
  - 后端通过 `kernel.CreateFunctionFromPrompt` 动态读取用户的配置，注册为高级 Agent 工作流。
- [x] **6.4 拥抱标准 MCP 协议 (Model Context Protocol)**
  - 在后端实现一个轻量级的 MCP Client 引擎。
  - 允许用户在设置页输入外部 MCP Server 的启动命令（例如 `npx @modelcontextprotocol/server-postgres ...`）。
  - C# 后端启动外部进程，使用 JSON-RPC 与子进程的 `stdio` 通信，拉取 `tools/list`，并将其动态注册到 Semantic Kernel 中，实现能力的无限扩展。

### 阶段七：Web Action (网页反向控制)
**目标**：打破浏览器的隔离，让 AI 能够直接操作用户当前正在浏览的网页（高亮、便签、整页截图、白名单页面脚本等）。后端通过 `rpc_request` / `rpc_response` 与 Chrome 侧栏闭环。

- [x] **7.1 后端双向 RPC 通信协议**
  - `WsMessage` 已支持 `rpc_request`、`rpc_response`；后端用等待机制在同一 WebSocket 连接上收发，避免阻塞消息循环（见 `Program.cs` 相关注释）。
- [x] **7.2 BrowserPlugin（内核工具）**
  - `BrowserPlugin` 已注册，例如 `highlight_webpage_text`、`add_floating_note`、`run_page_script`、`capture_full_page` 等；调用时向前端发送 `rpc_request`。
- [x] **7.3 前端 RPC 监听与调度（侧栏）**
  - 侧栏解析 `rpc_request`，按方法路由到 `chrome.scripting` 等执行。
- [x] **7.4 前端注入与回传**
  - 高亮、便签等由注入脚本完成，结果封装为 `rpc_response` 回后端。

**后续可增强（非阻塞）**：更丰富的 DOM/翻译类能力、与阶段七原文「划线」等文案对齐的更多工具；以产品需求为准迭代。

---

## ⚠️ 关键技术风险与应对

| 风险 | 影响 | 应对策略 |
|---|---|---|
| MV3 Service Worker 30 秒空闲终止 | WebSocket 连接断开 | WebSocket 由 Side Panel 持有，Service Worker 仅做事件调度 |
| COM Interop 与 AOT 不兼容 | 无法 AOT 编译 | 优先用 OpenXML SDK；打包用 `PublishSingleFile` 而非 AOT |
| localhost WebSocket 被恶意网页利用 | 本地命令被越权执行 | Origin 校验 + Token 认证（阶段一即实现） |
| 对话历史无限增长超出 Token 限制 | 模型调用失败 | 滑动窗口 + 摘要压缩策略 |
| COM 进程残留导致 Office 无法正常使用 | 用户体验差 | 严格的 COM 释放流程 + 兜底进程清理 |

---

## 🛠️ 文档与维护

基建与主线里程碑（阶段一至七）已在当前仓库落地。后续迭代请以代码为准，并参考：

- [README.md](../README.md)：安装、安全说明、本地开发命令。
- [未完成功能与能力缺口.md](./未完成功能与能力缺口.md)：宿主差异、配置边界与已知限制。
- [MCP与工具清单.md](./MCP与工具清单.md)：后端工具名与能力说明。

### 条件触发 backlog（与 `开源AI借鉴落地.md` 对齐）

- **P2 知识库 / RAG**：仅在出现明确召回质量或评测需求时再单独开任务；对照业界数据流做 chunk/评测，不替换后端 C# 栈。
- **轻量 Planner 会话状态**：仅在需要持久化检查点、硬限制单轮工具调用次数、或前端需固定阶段展示时再引入会话级小 DTO；当前以提示词 + `PlanPlugin` 注入 + HITL 为主，见 [开源AI借鉴落地.md](./开源AI借鉴落地.md) §3.2。
