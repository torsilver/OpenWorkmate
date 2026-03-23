---
name: ""
overview: ""
todos: []
isProject: false
---

# Chrome 侧栏 + 跨 Agent 指挥 — 计划（已按产品取舍修订）

## 产品决策（更新）

- **Chrome 不做「每标签页一个 Agent」**：侧栏维持**单一 `sessionId` / 单一对话线程**即可，避免多页多会话带来的混乱与 tab 定向复杂度。
- **双向互操作**：**任意一端均可指挥另一端**（例如 Chrome → Word、Word → Chrome；同理 Excel / PowerPoint / WPS 等与 `create_cross_agent_task` 支持的 `targetClientType` 之间）。后端不区分「主从」，只认 **来源 session**、**目标 clientType**、可选 **targetSessionId**。
- **在任一端下指令**：用户**无需为了「下指令」而切到目标端**——由当前端模型调用 `create_cross_agent_task` 即可。
- **目标端收到推送后自动跑一轮（交付项）**：当 WebSocket 收到 `type: "cross_agent_task"` 且该连接即目标任务的目标 session 时，客户端应**自动发起一轮对话**（与手动发一条用户消息等价），使 `[ChatService](backend/ChatService.cs)` 拉取 pending、注入 system 并驱动模型执行；执行结束后模型调用 `complete_cross_agent_task`。  
  - **Word / WPS / Office 任务窗格**：实现 **auto-run on push**。  
  - **Chrome 侧栏**：作为目标端时同样 **auto-run on push**（与 Word 对称），并建议用可读系统提示替代当前 `handleMessage` 落入 `default` 的裸 JSON。

---

## 原「多页多 Agent」设想（已搁置）

此前曾考虑：每个网页独立对话 / 记忆 / 操作上下文。该方向与「Chrome 单 Agent」决策冲突，**不再作为 Chrome 扩展的主路径**；若未来仅做「研究用」可选实验，再单独开文档，不与此处主线混写。

---

## 与现有代码的契合度（保留仍有效的部分）

### 对话与 Chrome 侧

- `[ChatService](backend/ChatService.cs)` 按 `sessionId` 维护会话；Chrome 侧栏 `[getSessionId()](chrome-extension/sidepanel.js)` 使用 `sessionStorage` **单会话**，与「不做每页一 Agent」一致，**无需再绑 tabId ↔ sessionId**。

### 跨端派发（双向）

- `[CrossAgentTaskPlugin](backend/Plugins/CrossAgentTaskPlugin.cs)`：`create_cross_agent_task` 的 `targetClientType` 已包含 `chrome`、`office-word`、`office-excel`、`office-powerpoint`、`wps` 等；**Word → Chrome** 与 **Chrome → Word** 使用同一机制。
- 任务入库后向目标 session **WebSocket 推送** `{ type: "cross_agent_task", taskId, description }`；`[complete_cross_agent_task](backend/Plugins/CrossAgentTaskPlugin.cs)` 可向来源 session 推送 `cross_agent_task_completed`。
- **缺口（本计划要补齐）**：各客户端在收到 `cross_agent_task` 后 **自动 `send` 触发消息**（或走与 `send` 相同的入口），并处理 **并发**（例如用户正在输入时是否排队、是否可合并多条待办由一轮完成）。

### 记忆

- `[MemoryPlugin](backend/Plugins/MemoryPlugin.cs)` 默认按当前 `sessionId`；跨端协作不强制改记忆模型。

---

## 可行性小结（修订）


| 维度                                  | 结论                                        |
| ----------------------------------- | ----------------------------------------- |
| Chrome 单会话侧栏                        | 与当前实现一致。                                  |
| Chrome ↔ Word（及他端）互指                | 后端已支持；**前端 auto-run + UX** 为待实现对称能力。      |
| 用户不下指令端即可执行                         | 依赖 **目标端 auto-run on push**；实施后符合「不切目标端」。 |
| 多 Chrome tab 各一 Agent + tab 定向浏览器操作 | **不做**（产品搁置）。                             |


---

## 建议实现顺序（后续执行时参考）

1. **统一约定**：触发文案（或独立 `type` 消息）需让模型明确「仅处理系统注入的跨端待办并 `complete_cross_agent_task`」，避免与用户正在编辑的草稿混淆；必要时 **队列化** 多条推送。
2. **Office / WPS 任务窗格**：在 WebSocket `message` 处理中识别 `cross_agent_task` → 调用与「用户点击发送」相同的发送路径（自动一轮）。
3. **Chrome 扩展 `[sidepanel.js](chrome-extension/sidepanel.js)`**：同上；并增加用户可见的简短系统提示（非裸 JSON）。
4. **（可选）提示词 / 工具说明**：多实例时 `targetSessionId` 的说明；单实例场景可只填 `targetClientType`。

---

## 与旧版「一页一 Agent」计划的关系

旧续篇中「tabId ↔ sessionId」「侧栏列出其他 Chrome Agent」「Browser 按 tabId 执行」等条目 **随 Chrome 多 Agent 搁置而不再跟进**；本文件主线为 **单 Chrome Agent + 各 Office 端与 Chrome 双向跨端任务**，且 **目标端推送后自动跑一轮** 为明确交付目标。