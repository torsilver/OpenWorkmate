# Office Copilot（_Taskly）项目结构 — 多维视图

本文用 **Markdown + Mermaid** 从多个角度描述仓库与运行时结构，便于新人定位代码与排障。  
在支持 Mermaid 的编辑器中打开本文件即可预览图；GitHub 对 Mermaid 也有基础支持。

> **说明**：图中名称与路径以当前仓库为准；默认监听端口以 `appsettings` / 扫描结果为准（常见为 `8765` 起跳）。  
> **编排栈**：主会话已迁移至 **MAF + MEAI**（`IChatClient` / `ChatClientAgent`），不再使用 Semantic Kernel；详见 [`maf-migration-baseline.md`](./maf-migration-baseline.md)。已移除的 MAF 宿主调试端点见 [`maf-host-debug-removal.md`](./maf-host-debug-removal.md)。

---

## 1. 仓库顶层（物理目录）

```mermaid
flowchart TB
  subgraph repo["仓库根 _Taskly"]
    BE["backend<br/>ASP.NET Core 本地服务<br/>OfficeCopilot.Server"]
    BT["backend.Tests<br/>xUnit：Unit / Integration"]
    CE["chrome-extension<br/>Chrome MV3 侧栏与选项页"]
    DOC["docs<br/>设计与清单类文档"]
    OA["office-addin<br/>Office 相关加载项"]
    WPS["wps-addin-new<br/>WPS 插件（业务以这里为准）"]
    HW["HelloWps<br/>官方模板/对照，勿直接改业务"]
    SU["shared-ui<br/>可复用前端资源（若有引用）"]
    REL["release<br/>发布产物"]
    SCR["scripts<br/>构建/辅助脚本"]
    CUR[".cursor<br/>规则与 IDE 配置"]
  end
```

---

## 2. 运行时拓扑（谁连谁）

```mermaid
flowchart LR
  subgraph clients["客户端"]
    EXT["Chrome 扩展<br/>sidepanel / options / …"]
    WPSUI["WPS 任务窗格<br/>wps-addin-new"]
    OFF["Office 加载项<br/>office-addin"]
  end

  subgraph local["本机 Office Copilot Server"]
    HTTP["HTTP<br/>REST /api/*<br/>静态页 debug/logs 等"]
    WS["WebSocket<br/>默认 /ws<br/>主对话通道"]
    WSSTT["WebSocket<br/>/api/stt-stream<br/>流式 ASR"]
  end

  subgraph external["外部能力"]
    LLM["兼容 OpenAI 的<br/>聊天 / Embedding API"]
    MCP["MCP 子进程<br/>McpClientManager"]
    FS["本机文件系统<br/>Office 文档等"]
  end

  EXT --> HTTP
  EXT --> WS
  EXT --> WSSTT
  WPSUI --> HTTP
  WPSUI --> WS
  OFF --> HTTP
  OFF --> WS

  HTTP --> LLM
  WS --> LLM
  MCP --- local
  local --> FS
```

**要点**：扩展与 Office/WPS 通过 **端口扫描 + `/api/bootstrap/local-service-auth`** 等发现本机服务；主交互在 **WebSocket**（配置项 `WebSocket:Path`，默认 `/ws`）。**WPS**：加载项在 `set_context` 中上报 **`wpsHostKind`**（`word` / `et` / `wpp` 等），后端据此在 `clientType=wps` 时可选收窄 **`CurrentDocument`** 具名工具及动态工具索引，与 `office-*` 子集对齐；未上报或 `unknown`/`none` 时不收紧（见 `ClientTypeToolFilter`、`docs/应用内AI插件列表.md` §三）。

---

## 3. 后端 `backend` 目录分层

```mermaid
flowchart TB
  subgraph entry["入口与宿主"]
    PROG["Program.cs<br/>DI 注册 / 中间件 / 路由 / WS 循环"]
    CHAT["ChatService（+ partial）<br/>RebuildRuntimeAsync、会话、MAF 流式对话"]
  end

  subgraph maf["MAF / MEAI / Workflows"]
    MAF["Services/Maf/*<br/>主会话流式、工具门面、中间件"]
    WF["Services/Chat/*<br/>ChatTurnWorkflow、阶段 Executor"]
    TREG["ToolRegistry + ToolInvocationMiddleware<br/>AIFunction / `[ToolFunction]` 插件"]
  end

  subgraph plugins["Plugins/*"]
    PL["Excel / Word / Ppt / File / System / Browser / …<br/>Memory / Plan / MCP 桥 / CLI / STT / OCR / …"]
  end

  subgraph services["Services/*"]
    SVC["Config / Skill / Memory / Plan / ScheduledTask<br/>CrossAgent / STT / OCR / Rpc / Hitl / Context …"]
  end

  subgraph infra["横切与集成"]
    MCPPKG["Mcp/*<br/>McpClient、Manager、McpKernelPlugin"]
    SEC["Security/*<br/>监听地址、CORS、本地 API 鉴权、发现文件"]
    TINV["Services/ToolInvocation/*<br/>SecurityPipeline、工具状态通知"]
  end

  PROG --> CHAT
  CHAT --> MAF
  CHAT --> WF
  CHAT --> TREG
  CHAT --> PL
  CHAT --> SVC
  CHAT --> MCPPKG
  PROG --> SEC
  CHAT --> TINV
```

---

## 4. `ChatService` 核心依赖（简化）

```mermaid
flowchart LR
  CS["ChatService"]

  CS --> CFG["ConfigService"]
  CS --> SKL["SkillService"]
  CS --> MCP["McpClientManager"]
  CS --> DT["DynamicTooling<br/>ToolCatalogIndex + AgentToolingPlugin"]
  CS --> VS["IVectorStore"]
  CS --> CRA["IChatRuntimeAccessor<br/>ToolRegistry + 模型 IChatClient"]
  CS --> EP["EmbeddingProvider"]
  CS --> PS["IPlanStore"]
```

配置或技能变更时会触发 **`RebuildRuntimeAsync`**（重建 `ToolRegistry`、模型客户端与 MCP 绑定等，见 `ChatService`）。

---

## 5. 主对话 WebSocket 消息流（概念）

```mermaid
sequenceDiagram
  participant C as 客户端<br/>扩展 / 任务窗格
  participant W as WebSocket /ws
  participant SM as SessionManager
  participant H as HandleSessionAsync
  participant CH as ChatService
  participant MAF as MAF ChatClientAgent<br/>+ ToolRegistry

  C->>W: 连接 token / sessionId / clientType
  W->>SM: Add(session)
  loop 收消息
    C->>H: JSON WsMessage
    H->>CH: 路由到业务处理
    CH->>MAF: 流式补全 + 工具调用（Workflow / Middleware）
    MAF-->>CH: 流式 token / 工具结果
    CH-->>H: 流式下行
    H-->>C: stream_chunk / stream_end 等
  end
```

（RPC、HITL、附件缓存等在同一 `HandleSessionAsync` 链路中按需介入，此处不展开每一条分支。）

---

## 6. HTTP `/api/*` 分组（按职责）

```mermaid
flowchart TB
  subgraph boot["发现与配置"]
    A1["GET /api/bootstrap/local-service-auth"]
    A2["GET|POST /api/config"]
    A3["POST /api/config/test-*"]
  end

  subgraph content["技能 / 记忆 / 计划 / 任务"]
    B1["/api/skills"]
    B2["/api/memory、/api/rag/ingest"]
    B3["/api/plans"]
    B4["/api/scheduled-tasks"]
    B5["/api/accurate-data"]
  end

  subgraph media["语音与会议"]
    C1["POST /api/transcribe"]
    C2["/api/meeting-transcript/*"]
  end

  subgraph misc["其它"]
    D1["GET /api/tools/builtin"]
    D2["GET /health"]
    D3["/api/debug/*"]
  end
```

完整路由以 `Program.cs` 中 `MapGet` / `MapPost` 等为准。

---

## 7. 本机数据与配置文件（概念）

```mermaid
flowchart TB
  subgraph appdata["%LocalAppData%\\OfficeCopilot"]
    UC["user-config.json<br/>用户配置（含密钥等）"]
    LS["local-service.json<br/>本机服务发现信息"]
    PLANS["Plans\\<br/>IPlanStore 默认目录"]
    ST["ScheduledTasks\\<br/>定时任务元数据"]
    RAG["rag.db 等<br/>向量库路径可配置"]
    CAT["CrossAgentTasks.db<br/>跨 Agent 任务 SQLite"]
  end

  subgraph repo2["仓库 / 进程"]
    LOGS["logs\\office-copilot-*.txt"]
    APPSET["appsettings*.json"]
  end
```

---

## 8. Chrome 扩展主要页面与脚本

```mermaid
flowchart LR
  subgraph pages["页面 HTML"]
    SP["sidepanel.html<br/>主聊天"]
    OP["options.html<br/>设置"]
    PL["plans.html<br/>计划"]
    ML["meeting-live.html<br/>会议相关"]
    WS2["workspace.html"]
    DS["debug-stats.html"]
  end

  subgraph js["对应逻辑"]
    SPJ["sidepanel.js"]
    OPJ["options.js"]
    PLJ["plans.js"]
    MLJ["meeting-live.js"]
    BG["background.js"]
    LSR["local-service-resolve.js"]
  end

  pages --> js
  BG --- SPJ
  LSR --- SPJ
```

---

## 9. `backend.Tests` 测试布局

```mermaid
flowchart TB
  T["backend.Tests"]

  T --> U["Unit/<br/>纯逻辑、解析、安全规则、<br/>工具/协议片段等"]
  T --> I["Integration/<br/>WebApplicationFactory<br/>HTTP 契约与鉴权等"]

  U --> X["xUnit + 断言"]
  I --> X
```

运行：`dotnet test backend.Tests/backend.Tests.csproj`；筛选 `FullyQualifiedName~Unit` 或 `~Integration`。

---

## 10. 后台托管服务（HostedService）

```mermaid
flowchart LR
  H1["ChatServiceWarmupHostedService<br/>启动预热运行时（RebuildRuntimeAsync）"]
  H2["ScheduledTaskRunnerService<br/>定时任务调度执行"]
  PROG2["Program.cs<br/>AddHostedService"]
  PROG2 --> H1
  PROG2 --> H2
```

---

## 11. Harness 与工具契约（驾驭工程）

对照 `.cursor/rules/harness-engineering.mdc`：Agent 可靠性优先靠**环境与边界**，而非只加长提示词。

**改插件工具函数时建议同步检查：**

1. 函数上的 `[Description]`（及 `[ToolFunction]` 元数据）是否写清输入形状与失败时模型可执行的重试方式（另见 `error-visibility`）。
2. [`docs/提示词清单.md`](docs/提示词清单.md) 中与默认 system 相关的句子是否与 [`ConfigService`](backend/ConfigService.cs) 一致。
3. 主会话动态工具：`ToolCatalogIndex` 检索质量、`AgentToolingPlugin` 的 `[Description]` 与 [`DynamicToolingInstruction`](backend/Services/DynamicTooling/DynamicToolingInstruction.cs) 是否与 `Program.cs` 注册的工具一致；计划撰写见 `PlanPlugin` + `PlanAuthoringToolDigest`。
4. 若涉及**多行/结构化字符串**工具参数，优先在服务端做确定性解析（例如 Word `paragraphs`、PPT `bodyText` 经 `ToolMultilineTextNormalizer`），并补 [`backend.Tests/Unit`](backend.Tests/Unit) 单测。
5. **用户技能**（`SkillAuthorPlugin` 生成内容）中列举的插件名应与上述字典及真实注册名一致，避免技能误导后续工具选择。

---

## 维护建议

- **改路由或契约**：同步更新本文件中的 API 分组图，并优先对照 `.cursor/rules` 里的 `api-json-contract` / `api-frontend-backend-contract`。
- **改工具或插件**：对照上文 **§11 Harness 与工具契约** 的检查清单。
- **新增大模块**：在「后端分层」或「运行时拓扑」中补一个子图即可，避免单图节点过多导致 Mermaid 难以阅读。

如需把某一维拆成独立短文（例如只画 MCP 生命周期），可在 `docs/` 下新增专题 MD 并链回本文。
