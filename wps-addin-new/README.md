# WPS 插件（wps-addin-new）

Open Workmate 的 **WPS 任务窗格** 客户端，与本地 `OpenWorkmate.Server` 通过 WebSocket 对话。**产品行为以 [chrome-extension/](../chrome-extension/) 为规范源**（对齐 Chrome 侧栏协议与交互）。

## 开发与验证

- **WPS 内验证**：使用 `wpsjs debug`（见仓库 [.cursor/rules/wps-plugin-dev.mdc](../.cursor/rules/wps-plugin-dev.mdc)）。
- **仅 Web 构建**：`npm run dev` / `npm run build` 不等于 WPS 宿主内可运行状态。
- **文字 / 表格 / 演示**：`wpsjs` 默认只按单个 `addonType` 注册。本项目在 `package.json` 中配置了 `wpsAddonTypes`（`wps`、`et`、`wpp`），并通过 **`patch-package`** 修补的 `wpsjs@2.2.3`（[`patches/wpsjs+2.2.3.patch`](patches/wpsjs+2.2.3.patch) + `postinstall`）写入多条 `publish.xml` / jsplugins 记录；Vite 将 `/wps-addon-scope/{wps|et|wpp}/` 前缀映射到站点根。**clone 或改依赖后请执行 `npm install` 以应用补丁。**  
- **PDF**：WPS 的 PDF 阅读窗口通常不提供与文字/表格/演示相同的 JS 加载项宿主，**加载项不会出现在 PDF 中**，属产品限制而非本仓库配置遗漏。

```sh
npm install
npm run build
```

## 任务窗格（唯一实现）

任务窗格与宿主 RPC 仅在 **`src/components/TaskPane.vue`** + **`src/composables/useOpenWorkmate.js`**（Vue 路由 **`#/taskpane`**）中维护；功能区 [`public/ribbon.xml`](public/ribbon.xml) 与 [`src/components/ribbon.js`](src/components/ribbon.js) 打开的即是该 Vue 页。

## 与 Chrome 对齐矩阵（WebSocket / HTTP）

| 能力 | Chrome 参考 | WPS Vue（`useOpenWorkmate.js`） |
|------|-------------|---------------------------|
| `text` / `planId` / `attachments` / `stop` | `sidepanel.js` | 有 |
| `stream_*`、`reasoning_chunk`、`blockSeq`/`blockKind` | 有 | 有 |
| `agent_status` / `agent_trace` / `agent_phase` | 有 | 有 |
| `tool_call_delta`（含 `isSubtask`） | 有 | 有（时间线 `tool-draft` / `subtask-tool-draft`） |
| `tool_invocation_*` + 计划 checklist | 有 | 有 |
| `ask_options_request` / `ask_options_response` | 有 | 有 |
| `ui_theme_changed` | 有 | 有（依赖根目录 `index.html` 引入的 `/OpenWorkmate-theme-boot.js`） |
| `set_context`（WPS 用 `pageTitle` 传文档上下文） | `pageTitle` | 连接后发送当前文档标识 |
| WS `agentProfileId` + `/api/config` 中 profiles | 有 | 有（`localStorage.activeAgentProfileId`） |
| 历史对话 `GET/DELETE /api/chat-sessions`、切换会话 | 有 | 有 |
| 会议 ASR、`workspace.html`、浏览器 RPC | Chrome 专属 | **不对齐**（宿主不同） |
| 完整选项页（模型/密钥等） | `options.html` | **不对齐**：标题栏 ⚙️ 用 `window.open` 打开 `chrome-extension://<ID>/options.html`（需配置 `VITE_CHROME_EXTENSION_ID`） |

**CurrentDocument（Word/Excel/PPT）RPC**：在 [`src/wps-rpc/`](src/wps-rpc/) 与 [`useOpenWorkmate.js`](src/composables/useOpenWorkmate.js) 中维护；与 Office 任务窗格 JSON 形状对齐时对照 [`office-addin/taskpane.js`](../office-addin/taskpane.js)。PPT 能力边界见仓库 [`docs/未完成功能与能力缺口.md`](../docs/未完成功能与能力缺口.md)。

## Chrome 扩展 ID（「设置」按钮）

任务窗格 **⚙️ 设置** 依赖 Vite 环境变量 **`VITE_CHROME_EXTENSION_ID`**（Chrome → 扩展程序 → 开发者模式 → Open Workmate 卡片上的 ID）。在 `wps-addin-new` 下创建 `.env.development.local` 或 `.env.local`，例如：

```env
VITE_CHROME_EXTENSION_ID=你的扩展ID
```

未配置时点击设置会弹出说明，避免静默失败。

## 宿主 RPC 技术债（Backlog）

部分能力仍受 **WPS 宿主版本 / JSAPI** 限制（例如 `ppt_slide_delete` 等分支的明确失败提示）。与 Chrome 浏览器专属能力不对标；新增或调整宿主 RPC 时在 `useOpenWorkmate.js` 与 `src/wps-rpc/` 中扩展即可。

## 相关文档

- [docs/用户请求-端到端时间线.md](../docs/用户请求-端到端时间线.md)
- [.cursor/rules/multi-client-chrome-canonical.mdc](../.cursor/rules/multi-client-chrome-canonical.mdc)
