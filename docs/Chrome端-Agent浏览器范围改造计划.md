# Chrome 端 Agent「浏览器范围」改造计划

## 1. 目标与原则

- **产品心智**：Agent 是「同一 Chrome 用户配置（profile）下的浏览器助手」，而非「绑定打开连接时那一页」。
- **与现有一致**：后端已按 `sessionId` 会话工作，未持久绑定 `tabId`；工具默认操作「当前窗口当前活动标签」——改造侧重**上下文同步、范围说明、可选会话统一**，而非推翻 RPC 模型。
- **权威端**：实现与文案以 `chrome-extension/` 为准（见仓库 multi-client 规则）。

## 2. 现状摘要（基线）


| 项             | 现状                                                     |
| ------------- | ------------------------------------------------------ |
| `sessionId`   | `sidepanel.js` 中存 `sessionStorage`，多窗口各开侧栏时可能多条会话      |
| `set_context` | 仅在 WS `open` 时发送当前活动页 **title**，用于服务端 DisplayName      |
| 标签切换          | 无 `tabs.onActivated` / `onUpdated`，标题与 DisplayName 易滞后 |
| `tab_list`    | `chrome.tabs.query({ currentWindow: true })`，仅当前窗口     |
| 工具默认目标        | `active: true, currentWindow: true`                    |


## 3. 改造项（分阶段）

### 阶段 A — 上下文与 UI（低风险，优先）

**A1. 活动标签变化时同步上下文**

- 在 `sidepanel.js` 注册 `chrome.tabs.onActivated`、`chrome.tabs.onUpdated`（需防抖/去重，避免 URL/title 抖动风暴）。
- 当**当前窗口**活动标签变化时：更新侧栏「当前页」展示；若 WS 已连接，发送 `set_context`（可扩展 payload，见 A2）。
- 注意：侧栏自身所在扩展页不应误当成「用户网页」；仅当 `changeInfo`/`tab` 表示用户前台页变化时更新（以 `active: true, currentWindow: true` 查询结果为准）。

**A2. 丰富 `set_context`（可选，需前后端契约）**

- 方案 A（最小）：仍只发 `pageTitle`，但保证为**最新**活动页标题（阶段 A1 已足够闭环 DisplayName）。
- 方案 B：增加字段，例如 `activeTabId`、`url`（可截断）、`windowId`，供日志与 future 使用；后端 `MessageRouter` / `Program.cs` 中 `set_context` 分支需同步解析（仅存储展示用，不参与权限逻辑）。
- **建议**：先做 A1 + 方案 A；B 在确有产品需求再做（避免 DTO 膨胀）。

**A3. 文案与侧栏标签**

- `sidepanel.html` / 相关 copy：将「当前页面」弱化为「当前活动标签」或「浏览器上下文」，避免用户以为 Agent 锁死在首次连接那一页。

### 阶段 B — 多标签/多窗口范围（中风险）

**B1. `tab_list` 范围可配置或分级**

- 保留默认：`currentWindow: true`（与现提示词/习惯一致）。
- 新增可选：例如 `scope: "browser"` 或单独 `scriptId` `tab_list_all_windows`，使用 `chrome.tabs.query({})` 并限制 `maxTabs`、对 URL 做脱敏/截断，防止上下文爆炸。
- 同步更新 `BrowserPlugin.cs` 中 `run_page_script` 的 Description，避免模型误用。

**B2. 预定义脚本与 `tabId`**

- 评估：DOM 类脚本是否支持可选 `tabId`（默认仍当前活动页），减少「必须先 tab_activate」的轮次；需逐项评估注入权限与 HITL 策略。

### 阶段 C — 严格「单 profile 单会话」（可选，架构）

- 将 `sessionId` 从仅 `sessionStorage` 改为：**首次** `chrome.storage.local` 生成并复用（或用户「新对话」时轮换），使多窗口侧栏共享同一会话（需定义：多侧栏是否共享同一条 WS、谁负责重连）。
- **风险**：多窗口同时开侧栏时 UI 状态、流式输出、HITL 弹窗冲突；需单独 UX 设计。
- **建议**：作为独立里程碑，在 A+B 稳定后再做。

## 4. 涉及文件（预期）


| 文件                                 | 改动要点                                     |
| ---------------------------------- | ---------------------------------------- |
| `chrome-extension/sidepanel.js`    | 监听 tab 事件、`set_context`、可选 `tab_list` 扩展 |
| `chrome-extension/sidepanel.html`  | 文案                                       |
| `backend/MessageRouter.cs`         | 若做 A2 方案 B：扩展 `set_context` DTO          |
| `backend/Program.cs`               | `set_context` 处理                         |
| `backend/Plugins/BrowserPlugin.cs` | 工具 Description 与行为说明                     |
| `docs/提示词清单.md`                    | 若系统提示涉及「当前页」语义，随功能同步（有变更时）               |


## 5. 测试建议

- 手工：连接后切换标签 → 侧栏标题与（若对接 DisplayName）服务端展示是否更新；工具是否在**新**活动页执行。
- 手工：`tab_list` 在仅当前窗口与「全浏览器」两种模式下行为符合预期。
- 回归：`tab_close` 仍禁止关当前活动页；`chrome://` 页失败提示仍清晰（error-visibility）。

## 6. 验收标准（阶段 A）

- 用户切换当前窗口活动标签后，无需重连，侧栏展示的「当前页」与发往服务端的 `set_context`（至少 title）与活动标签一致。
- 文档/文案不再暗示「Agent 只服务连接时的那一页」。

## 7. 建议排期

1. **Sprint 1**：阶段 A（A1 + A3，A2 方案 A）。
2. **Sprint 2**：阶段 B1（`tab_list` 扩展）+ 提示词/工具说明。
3. **Backlog**：B2、`tabId` 参数化；阶段 C。

---

*文档版本：与 2026-04-09 讨论结论对齐；实施时可按实验性项目规则直接做破坏性调整，无需兼容旧字段。*