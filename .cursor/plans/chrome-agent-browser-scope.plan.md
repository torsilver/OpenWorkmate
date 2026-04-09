---
name: Chrome Agent 浏览器范围（阶段 A→B）
overview: 依据 docs/Chrome端-Agent浏览器范围改造计划.md，优先落地活动标签上下文同步与侧栏文案；可选扩展 tab_list 全浏览器范围与工具说明。
---

# Chrome 端 Agent「浏览器范围」— Cursor 执行计划

> **设计依据**（勿删）：[`docs/Chrome端-Agent浏览器范围改造计划.md`](../../docs/Chrome端-Agent浏览器范围改造计划.md)  
> **规范**：浏览器端以 `chrome-extension/` 为权威；错误须可感知（`error-visibility`）；本仓库为实验性可无兼容负担。

## 如何一键 Build

1. 在 Cursor **Agent** 输入框用 **`@`** 引用本文件：`.cursor/plans/chrome-agent-browser-scope.plan.md`（或从 **Plans** 面板打开后点 **Build**）。
2. 发送下方 **一键提示词**（可复制整段），Agent 应按阶段顺序勾选完成下列任务。

### 一键提示词（复制到 Agent）

```text
请严格按仓库内 @.cursor/plans/chrome-agent-browser-scope.plan.md 中的勾选清单顺序实现：
- 先完成「阶段 A」全部项并自测，再询问我是否继续阶段 B。
- 改动范围仅限该计划中列出的文件与行为；遵守 multi-client-chrome-canonical 与 error-visibility。
- 阶段 A 不要求改后端 DTO（仅发 pageTitle 的 set_context）。
- 完成后在回复中列出修改的文件路径与手工验证步骤。
```

---

## 阶段 A — 上下文与 UI（必做，Sprint 1）

### A1 — 活动标签变化时同步标题与 `set_context`

- [ ] 在 [`chrome-extension/sidepanel.js`](../../chrome-extension/sidepanel.js) 中实现：
  - [ ] 注册 `chrome.tabs.onActivated`：在**当前窗口**用户切换到新活动标签后，调用与现有逻辑一致的「解析活动标签标题」路径（复用或抽取 `getActiveTabTitle`）。
  - [ ] 注册 `chrome.tabs.onUpdated`：仅在可能影响标题/活动性的情况下更新（例如 `changeInfo` 含 `title` 或 `status === "complete"`），且**防抖**（建议 150–300ms）或去重，避免同一标签连续事件刷屏。
  - [ ] 更新侧栏 UI：调用现有 `updateCurrentPageLabel(title, getSessionId())`（或等价），保证与 `getActiveTabTitle()` 结果一致。
  - [ ] 若 `ws.readyState === WebSocket.OPEN`，调用现有 `sendSetContext(pageTitle)`；`pageTitle === "(无)"` 时行为与现有一致（可不发或早退）。
  - [ ] **不要**把扩展侧栏页、不可注入页误报成「用户页」：以 `chrome.tabs.query({ active: true, currentWindow: true })` 结果为准；`chrome://` 等与现 `getActiveTabTitle` 一致返回 `(无)`。
- [ ] 监听器在侧栏生命周期内只注册一次（例如 `DOMContentLoaded` 后）；避免重复绑定导致多次 `set_context`。

### A3 — 侧栏文案

- [ ] 在 [`chrome-extension/sidepanel.html`](../../chrome-extension/sidepanel.html)（及相关可见文案）中，将暗示「绑定某一页」的措辞改为「当前活动标签」或「浏览器上下文」等，与 [`docs/Chrome端-Agent浏览器范围改造计划.md`](../../docs/Chrome端-Agent浏览器范围改造计划.md) 第六节验收一致。

### 阶段 A 验收（Agent 自检清单）

- [ ] 已连接 WS 时切换当前窗口活动标签：侧栏标签文案与**新**活动页一致。
- [ ] 无需重连 WebSocket，服务端 DisplayName 随 `set_context` 更新（至少 title 路径）。
- [ ] 快速切换标签 / 页面加载中：无控制台异常风暴、无未捕获错误。

---

## 阶段 B — `tab_list` 范围扩展（选做，Sprint 2）

> 默认：**先完成阶段 A 并经用户确认后再做。**

### B1 — 全浏览器标签列表（可选 scriptId 或参数）

- [ ] 在 [`chrome-extension/sidepanel.js`](../../chrome-extension/sidepanel.js) 的 `runExtensionPageScript` / `EXTENSION_PAGE_SCRIPT_IDS` 中：
  - [ ] 保留现有 `tab_list` 行为：`chrome.tabs.query({ currentWindow: true })`。
  - [ ] 新增其一：**`tab_list_all_windows`**（新 scriptId）或 `tab_list` 支持 `params.scope === "browser"`，使用 `chrome.tabs.query({})`；遵守 `maxTabs` 上限；URL 过长时截断；返回结构清晰（含 `tabId`、title、url 片段、windowId、active）。
- [ ] 在 [`backend/Plugins/BrowserPlugin.cs`](../../backend/Plugins/BrowserPlugin.cs) 的 `run_page_script` 工具 **Description** 中写明两种范围及何时用哪一种，避免模型滥用全浏览器列表。

### 阶段 B 验收

- [ ] 手工：`tab_list` 与新模式下列表范围符合预期；条数受 `maxTabs` 约束。

---

## 阶段 C — 单 profile 单 `sessionId`（Backlog，本计划默认不做）

- [ ] **不在本轮实现**。若未来要做：将 `sessionId` 迁入 `chrome.storage.local` 并处理多窗口侧栏并发；单独开计划。

---

## 参考代码锚点（便于 Agent 定位）

| 用途 | 文件与逻辑 |
|------|------------|
| `set_context` 发送 | `sidepanel.js` → `sendSetContext`、`connect` 内 `open` |
| WS 收 RPC | `sidepanel.js` → `handleRpcRequest`、`executeInActiveTab` |
| 服务端 DisplayName | `backend/Program.cs` → `case "set_context"` |
| 工具说明 | `backend/Plugins/BrowserPlugin.cs` → `RunPageScriptAsync` Description |

---

## 测试命令（后端若未改则非必须）

```bash
dotnet test d:/CodeBase/_Taskly/backend.Tests/backend.Tests.csproj
```

Chrome 侧以手工验证为主（见阶段 A/B 验收）。
