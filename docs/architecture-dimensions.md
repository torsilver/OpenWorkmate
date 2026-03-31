# Office Copilot（_Taskly）项目结构 — 多维视图

本文用 **Markdown + Mermaid** 从多个角度描述仓库与运行时结构，便于新人定位代码与排障。  
在支持 Mermaid 的编辑器中打开本文件即可预览图；GitHub 对 Mermaid 也有基础支持。

> **说明**：图中名称与路径以当前仓库为准；默认监听端口以 `appsettings` / 扫描结果为准（常见为 `8765` 起跳）。

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

**要点**：扩展与 Office/WPS 通过 **端口扫描 + `/api/bootstrap/local-service-auth`** 等发现本机服务；主交互在 **WebSocket**（配置项 `WebSocket:Path`，默认 `/ws`）。

---

## 3. 后端 `backend` 目录分层

```mermaid
flowchart TB
  subgraph entry["入口与宿主"]
    PROG["Program.cs<br/>DI 注册 / 中间件 / 路由 / WS 循环"]
    CHAT["ChatService（+ partial）<br/>Kernel 生命周期、会话、流式对话"]
  end

  subgraph sk["SemanticKernel"]
    SKDIR["Services/SemanticKernel/*<br/>流式工具编排、子 Agent、回合协调"]
  end

  subgraph plugins["Plugins/*"]
    PL["Excel / Word / Ppt / File / System / Browser / …<br/>Memory / Plan / MCP 桥 / CLI / STT / OCR / …"]
  end

  subgraph services["Services/*"]
    SVC["Config / Skill / Memory / Plan / ScheduledTask<br/>CrossAgent / STT / OCR / Rpc / Hitl / Context …"]
  end

  subgraph infra["横切与集成"]
    MCPPKG["Mcp/*<br/>McpClient、Manager、Kernel 插件"]
    SEC["Security/*<br/>监听地址、CORS、本地 API 鉴权、发现文件"]
    FILT["Filters/*<br/>SecurityFilter、会话上下文等"]
  end

  PROG --> CHAT
  CHAT --> SKDIR
  CHAT --> PL
  CHAT --> SVC
  CHAT --> MCPPKG
  PROG --> SEC
  CHAT --> FILT
```

---

## 4. `ChatService` 核心依赖（简化）

```mermaid
flowchart LR
  CS["ChatService"]

  CS --> CFG["ConfigService"]
  CS --> SKL["SkillService"]
  CS --> MCP["McpClientManager"]
  CS --> TS["IToolSelector / ToolSelectionService"]
  CS --> TI["IToolIndexService"]
  CS --> VS["IVectorStore"]
  CS --> KA["IKernelAccessor"]
  CS --> EP["EmbeddingProvider"]
  CS --> PS["IPlanStore"]
  CS --> REG["SkStreamChatToolingProcessRegistry"]
  CS --> SUB["SkSubtaskChatCompletionAgentRunner"]
  CS --> COO["IChatTurnProcessCoordinator"]
```

配置或技能变更时会触发 **重建 Kernel**（与 MCP、内置/用户工具索引同步相关逻辑在 `ChatService` 内）。

---

## 5. 主对话 WebSocket 消息流（概念）

```mermaid
sequenceDiagram
  participant C as 客户端<br/>扩展 / 任务窗格
  participant W as WebSocket /ws
  participant SM as SessionManager
  participant H as HandleSessionAsync
  participant CH as ChatService
  participant K as Semantic Kernel<br/>+ Plugins

  C->>W: 连接 token / sessionId / clientType
  W->>SM: Add(session)
  loop 收消息
    C->>H: JSON WsMessage
    H->>CH: 路由到业务处理
    CH->>K: 聊天补全 + 工具调用
    K-->>CH: 流式 token / 工具结果
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

  T --> U["Unit/<br/>纯逻辑、解析、安全规则、<br/>SK 编排片段等"]
  T --> I["Integration/<br/>WebApplicationFactory<br/>HTTP 契约与鉴权等"]

  U --> X["xUnit + 断言"]
  I --> X
```

运行：`dotnet test backend.Tests/backend.Tests.csproj`；筛选 `FullyQualifiedName~Unit` 或 `~Integration`。

---

## 10. 后台托管服务（HostedService）

```mermaid
flowchart LR
  H1["ChatServiceKernelWarmupHostedService<br/>启动预热 Kernel"]
  H2["ScheduledTaskRunnerService<br/>定时任务调度执行"]
  PROG2["Program.cs<br/>AddHostedService"]
  PROG2 --> H1
  PROG2 --> H2
```

---

## 维护建议

- **改路由或契约**：同步更新本文件中的 API 分组图，并优先对照 `.cursor/rules` 里的 `api-json-contract` / `api-frontend-backend-contract`。
- **新增大模块**：在「后端分层」或「运行时拓扑」中补一个子图即可，避免单图节点过多导致 Mermaid 难以阅读。

如需把某一维拆成独立短文（例如只画 MCP 生命周期），可在 `docs/` 下新增专题 MD 并链回本文。
