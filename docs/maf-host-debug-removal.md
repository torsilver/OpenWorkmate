# MAF 宿主调试能力移除说明

本文记录为**减少本机 HTTP 暴露面**而从后端删除的 Microsoft Agent Framework（MAF）相关**调试/实验入口**，并说明各自**原先的作用**与**当前状态**。

**结论（给排查用）**：Chrome / WPS / Office 与本地后台的**正常对话与配置**走 **WebSocket（`/ws`）** 与 **`/api/*`**，**不依赖**下文任何一项；移除后**不改变**三端 + 一后台的产品主路径。

---

## 一、MAF DevUI（`Microsoft.Agents.AI.DevUI`）

### 原先的作用

- NuGet 包 **`Microsoft.Agents.AI.DevUI`**（preview）提供 ASP.NET Core 扩展，在应用里注册 **DevUI**。
- **DevUI** 是一个**浏览器内嵌的调试对话页**（历史上映射路径为 **`/devui`**），用于在**不经过 Chrome 扩展 / 任务窗格 WebSocket** 的情况下，直接与本机注册的 MAF Agent 发消息，做最小联调。
- 与 DevUI 同一时期在开发环境注册的常见配套还包括：
  - **`AddAIAgent`**：向 MAF 宿主注册一个命名 Agent（例如 `OfficeCopilot`）。
  - **OpenAI Responses / Conversations 风格的 HTTP 端点**（`AddOpenAIResponses` / `AddOpenAIConversations` 及对应的 `Map*`），供 DevUI 或兼容客户端调用。

### 当前状态

- **已删除**：不再引用该 NuGet 包，不再调用 `AddAIAgent`、`MapDevUI`，不再映射上述 OpenAI 兼容调试端点。

---

## 二、MAF AG-UI（`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`）

### 原先的作用

- NuGet 包 **`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`**（preview）提供 **AG-UI** 协议在 ASP.NET Core 上的托管。
- **AG-UI** 在本项目中历史上映射为开发环境下的 **`/agui`**（常见形态为 **SSE** 流），用于按 AG-UI 标准事件流驱动 Agent，与现有**自定义 WebSocket 消息**形成「双轨 / 对照」实验，**并非**三端产品的正式协议。

### 当前状态

- **已删除**：不再引用该 NuGet 包，不再调用 `AddAGUI`、`MapAGUI`，**不提供** `/agui`。

---

## 三、项目内配套类型（仅服务于上述宿主调试）

以下类型**不参与**主会话 `ChatService` → `MafMainSessionStreamRunner` 等生产路径，仅为 DevUI / AG-UI 桥接运行时而存在；在移除宿主端点后一并删除。

| 文件（已删除） | 原先作用 |
|----------------|----------|
| `backend/Services/RuntimeDelegatingChatClient.cs` | 实现 `IChatClient`，将调用**委托**给 `IChatRuntimeAccessor` 上当前运行时模型，使 DevUI / AG-UI 在 DI 或 `Map` 阶段即可挂接「动态」聊天客户端。 |
| `backend/Services/Maf/AgUiEventMapping.cs` | 文档性质的 **WebSocket 消息类型 ↔ AG-UI 事件** 对照说明（及可选说明对象），**无**业务代码依赖；移除 AG-UI 后不再保留。 |

---

## 变更后主路径（便于对照）

| 能力 | 路径 / 方式 |
|------|-------------|
| 侧栏 / 任务窗格会话 | **WebSocket**：配置项中的 `WebSocket:Path`（默认 `/ws`） |
| 配置、引导、其它 HTTP API | **`/api/*`**（受 `LocalApiAuthMiddleware` 等策略约束） |
| 本机调试日志网页（Taskly 自有） | 静态页 **`/debug/logs.html`** 等（与 MAF DevUI/AG-UI **无关**） |

---

## 若将来需要恢复

- 需重新添加对应 NuGet 包，并在 `Program.cs`（或等价启动位置）恢复注册与 `Map*`；具体 API 以当时 MAF 包版本文档为准。
- 可能需从版本历史中恢复 **`RuntimeDelegatingChatClient`**（或按新 API 重写桥接）；**`AgUiEventMapping.cs`** 仅为对照表，可按需重写。

---

*文档与 `docs/maf-migration-baseline.md` 中「MAF 宿主调试 HTTP 端点」条目一致；代码以仓库当前状态为准。*
