# WPS 插件（wps-addin-new）

Office Copilot 的 **WPS 任务窗格** 客户端，与本地 `OfficeCopilot.Server` 通过 WebSocket 对话。**产品行为以 [chrome-extension/](../chrome-extension/) 为规范源**（对齐 Chrome 侧栏协议与交互）。

## 开发与验证

- **WPS 内验证**：使用 `wpsjs debug`（见仓库 [.cursor/rules/wps-plugin-dev.mdc](../.cursor/rules/wps-plugin-dev.mdc)）。
- **仅 Web 构建**：`npm run dev` / `npm run build` 不等于 WPS 宿主内可运行状态。

```sh
npm install
npm run build
```

## 双栈说明（Vue / public 遗留）

| 栈 | 路径 | 状态 |
|----|------|------|
| **规范实现** | `src/components/TaskPane.vue` + `src/composables/useCopilot.js` | 对齐 Chrome 的迭代以这里为准 |
| **遗留静态** | `public/taskpane.html`、`public/taskpane.js` | **冻结**：仅修安全或阻断性问题；新能力请迁到 Vue 栈 |

Ribbon 打开任务窗格应指向 **Vue 路由**（`#/taskpane`），勿把 `public/taskpane` 当作权威实现。

## 与 Chrome 对齐矩阵（WebSocket / HTTP）

| 能力 | Chrome 参考 | WPS Vue（`useCopilot.js`） |
|------|-------------|---------------------------|
| `text` / `planId` / `attachments` / `stop` | `sidepanel.js` | 有 |
| `stream_*`、`reasoning_chunk`、`blockSeq`/`blockKind` | 有 | 有 |
| `agent_status` / `agent_trace` / `agent_phase` | 有 | 有 |
| `tool_call_delta`（含 `isSubtask`） | 有 | 有（时间线 `tool-draft` / `subtask-tool-draft`） |
| `tool_invocation_*` + 计划 checklist | 有 | 有 |
| `ask_options_request` / `ask_options_response` | 有 | 有 |
| `ui_theme_changed` | 有 | 有（依赖根目录 `index.html` 引入的 `/taskly-theme-boot.js`） |
| `set_context`（WPS 用 `pageTitle` 传文档上下文） | `pageTitle` | 连接后发送当前文档标识 |
| WS `agentProfileId` + `/api/config` 中 profiles | 有 | 有（`localStorage.activeAgentProfileId`） |
| 历史对话 `GET/DELETE /api/chat-sessions`、切换会话 | 有 | 有 |
| 会议 ASR、`workspace.html`、浏览器 RPC | Chrome 专属 | **不对齐**（宿主不同） |
| 完整选项页（模型/密钥等） | `options.html` | **不对齐**：标题栏 ⚙️ 用 `window.open` 打开 `chrome-extension://<ID>/options.html`（需配置 `VITE_CHROME_EXTENSION_ID`） |

**CurrentDocument（Word/Excel）RPC**：`excel_*` 与 `word_insert_table` 在 **Vue** [`src/wps-rpc/`](src/wps-rpc/) 与 [`useCopilot.js`](src/composables/useCopilot.js) 中维护；[`public/taskpane.js`](public/taskpane.js) 中对应分支**未与 Vue 双写**，以 Vue 为准。PPT 相关仍见仓库 `docs/未完成功能与能力缺口.md` 与 `scripts/sync-public-ppt-rpc.mjs`。

## Chrome 扩展 ID（「设置」按钮）

任务窗格 **⚙️ 设置** 依赖 Vite 环境变量 **`VITE_CHROME_EXTENSION_ID`**（Chrome → 扩展程序 → 开发者模式 → Office Copilot 卡片上的 ID）。在 `wps-addin-new` 下创建 `.env.development.local` 或 `.env.local`，例如：

```env
VITE_CHROME_EXTENSION_ID=你的扩展ID
```

未配置时点击设置会弹出说明，避免静默失败。

## 宿主 RPC 技术债（Backlog）

`useCopilot.js` 中 `handleRpcRequest` 对 **Excel** 等多处仍为占位或「需按 WPS API 实现」的返回；**Word** 如 `word_insert_table` 等亦可能未实现。与 Chrome 浏览器工具不对标，按业务优先级在 WPS JS API 上逐项补全即可。

## 相关文档

- [docs/用户请求-端到端时间线.md](../docs/用户请求-端到端时间线.md)
- [.cursor/rules/multi-client-chrome-canonical.mdc](../.cursor/rules/multi-client-chrome-canonical.mdc)
