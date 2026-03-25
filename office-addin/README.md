# Office Copilot — Office 加载项（Word / Excel）

本目录为 **Office 加载项** 独立安装包，与 Chrome 扩展、WPS 加载项分文件夹存放，便于客户按需安装。

## 功能

- 在 Word 或 Excel 中打开任务窗格（侧边栏），与同一后台的 AI 对话。
- **不包含设置页**：AI 与后台地址请在 **Chrome 扩展** 中配置；本端仅连接已配置好的后台。
- 支持“当前文档”RPC：在 Word 中可插入正文、读取正文；在 Excel 中可读写选区。

## 开发与调试

1. **后台**：先启动本地后台（默认 `http://localhost:8765`），并在 Chrome 扩展中配置 AI。
2. **任务窗格托管**：Office 加载项要求任务窗格从 **HTTPS** 地址加载。开发时可选：
   - 用本地 HTTPS 服务托管 `office-addin/` 目录（如 `https://localhost:3000`），并在 `manifest.xml` 中把 `SourceLocation`、`IconUrl`、`AppDomains` 改为你的 base URL。
   - 或使用 ngrok 等将本地服务暴露为 HTTPS，并相应修改 manifest 中的 URL。
3. **安装加载项**：
   - Word/Excel：文件 → 选项 → 信任中心 → 信任中心设置 → 受信任的加载项目录，或通过“插入 → 加载项 → 上传我的加载项”加载本目录下的 `manifest.xml`（需先通过 HTTPS 提供 manifest 与 taskpane 的访问）。

## 文件说明

- `manifest.xml`：Office 加载项清单（Word + Excel 双宿主）。
- `taskpane.html` / `taskpane.css` / `taskpane.js`：任务窗格 UI 与 WebSocket 连接、RPC 处理（word_insert_text、word_read_body、excel_read_range、excel_write_range）。
- 图标：manifest 中默认指向 `https://localhost:3000/assets/icon-32.png` 与 `icon-64.png`，部署时请替换为实际 HTTPS 地址或放置对应资源。

## 连接参数

- WebSocket 默认：`ws://localhost:8765/ws`。若服务端配置了 `WebSocket:AuthToken`，请在任务窗格所在源的开发者工具中执行 `localStorage.setItem('tasklyLocalServiceAuthToken','你的密钥')` 后刷新（与 WPS 任务窗格相同键名）。
- 连接时携带 `clientType=office-word` 或 `clientType=office-excel`，便于后台识别客户端能力。
