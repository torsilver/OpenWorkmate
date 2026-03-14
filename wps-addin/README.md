# Office Copilot — WPS 加载项

本目录为 **WPS 加载项** 独立安装包，与 Chrome 扩展、Office 加载项分文件夹存放，便于客户按需安装。

## 功能

- 在 WPS 文字/表格中打开侧边栏或面板，与同一后台的 AI 对话。
- **不包含设置页**：AI 与后台地址请在 **Chrome 扩展** 中配置；本端仅连接已配置好的后台。
- 支持“当前文档”RPC：通过 `window.wps` 操作当前文档（需在 WPS 桌面版加载项环境中运行）。

## 开发与集成

- WPS 加载项开发请参考 [WPS 加载项开发说明](https://qn.cache.wpscdn.cn/encs/doc/office_v11/topics/WPS%20%E5%8A%A0%E8%BD%BD%E9%A1%B9%E5%BC%80%E5%8F%91/)，使用官方 wpsjs 等工具创建项目。
- 本目录提供与 Office 加载项同构的**聊天 UI、WebSocket 连接与 RPC 协议**，连接时携带 `clientType=wps`。
- 将本目录下的 `index.html`、`taskpane.css`、`taskpane.js` 集成到你的 WPS 加载项项目中，并在页面中引入 `taskpane.js`，确保在 WPS 环境中可访问 `window.wps`。
- 后台默认：`ws://localhost:8765/ws`，Token 与 Chrome 扩展开发 Token 一致。

## 文件说明

- `index.html`：侧边栏/面板页面结构（与 office-addin 任务窗格同构）。
- `taskpane.css`：样式，与 office-addin 共用风格。
- `taskpane.js`：WebSocket 连接（带 `clientType=wps`）、消息处理、RPC 处理（通过 `window.wps` 实现 word_insert_text、word_read_body、excel_read_range、excel_write_range 等，需在 WPS 环境中运行）。

## 连接参数

- WebSocket：`ws://localhost:8765/ws?sessionId=xxx&token=xxx&clientType=wps`
- 配置请在 Chrome 扩展中完成，本端不提供设置页。
