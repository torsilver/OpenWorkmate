# Office Copilot

Office Copilot 是一款基于本地 C# 服务与大语言模型（LLM）驱动的 Chrome 浏览器插件。它能够通过自然语言与你的本地电脑进行交互，帮助你自动化处理 Excel、Word 文档，执行系统命令，甚至在侧边栏中直接渲染数据图表。

## ✨ 核心特性

- **文档处理**：读取/写入 Excel 和 Word 文件。
- **系统控制**：执行本地系统命令（支持白名单安全拦截）。
- **完全本地化**：前端通过 WebSocket 连接本地服务，数据不经过第三方中转服务器。
- **动态展示板**：支持 AI 直接生成图表代码并在浏览器中渲染。
- **动态工作流 (Skills)**：你可以用自然语言编写业务 SOP，让 AI 自动组合执行多个步骤。
- **支持外接 MCP 生态**：内置原生 MCP Client，一键接入开源社区海量模型上下文协议 (Model Context Protocol) 插件（如本地数据库查询、网页搜索等）。

---

## 🚀 快速安装指南

### 1. 运行本地服务端

1. 进入 `release/backend` 目录。
2. 双击运行 `OfficeCopilot.Server.exe`。
   - 看到提示 `Now listening on: http://127.0.0.1:8765`（若 8765 被占用会自动尝试后续端口，如 8766）表示服务端启动成功。
   - 实际地址会写入 `%LocalAppData%\OfficeCopilot\local-service.json`（供排障）；Chrome 扩展与 WPS/Office 任务窗格会通过扫描 `127.0.0.1` 上的一段端口并请求引导接口自动发现服务，一般无需手改地址。
   - **单实例（Windows）**：同一用户会话下只会运行一个后台进程；若再次启动会提示「已有实例在运行」。若默认端口被**其它软件**占用，唯一实例会自动顺延到配置范围内的下一个可用端口（策略 A）。本地调试需要多开进程时可在命令行追加 `--allow-second-instance`（会从启动参数中剥离，不会传给 ASP.NET Core）。
   - **应用配置**统一保存在本机 **`%LocalAppData%\OfficeCopilot\user-config.json`**。首次启动若该文件不存在，会自动生成一份默认配置；之后在浏览器扩展设置页中保存的 API Key、访问密钥等也会写入此文件。请勿将含真实密钥的 `user-config.json` 提交到公开仓库。

### 2. 安装 Chrome 插件

1. 打开 Chrome 浏览器，在地址栏输入 `chrome://extensions/`。
2. 开启右上角的 **开发者模式**。
3. 点击 **加载已解压的扩展程序**。
4. 选择本项目中的 `chrome-extension` 文件夹（或者解压 `release/chrome-extension.zip` 后选择解压的目录）。
5. 插件安装成功后，点击浏览器扩展栏上的 **Office Copilot** 图标，即可在侧边栏打开聊天窗口。

### 3. 配置向导

第一次使用时，请点击侧边栏右上角的 ⚙️ 齿轮图标：
1. 在 **大模型设置** 页面填写你的 API Key 和模型（默认配置兼容 OpenAI / Moonshot / DeepSeek 等接口）。
2. 在 **自定义技能** 页面，你可以用自然语言写出“业务流程说明书”。
3. 在 **外部组件 (MCP)** 页面，如果你有需要，可以配置第三方 MCP 服务的启动命令（比如 `npx @modelcontextprotocol/server-postgres ...`）。

---

## 💡 使用示例

服务端运行且插件连接成功后（状态显示为绿色的“已连接”），你可以对它发送如下指令：

1. **执行系统命令**
   - *"帮我看看 D 盘根目录下有哪些文件"*
   - *"帮我用 ipconfig 查一下我的 IP 地址"*

2. **操作 Excel 文件**
   - *"读取 D:\test\sales.xlsx 的 Sheet1，从 A1 到 C5 的数据"*
   - *"帮我创建一个包含姓名和年龄的测试数据，保存到 D:\test\users.xlsx"*
   - **图表（xlsx 内原生图）**：后端 Excel 插件里与**工作簿嵌入式图表**相关的工具**目前仅有 `excel_charts_list`**（按工作表统计图表数量）。**不提供**插入、修改数据源或删除图表等能力（Open XML 可编程实现，但本仓库未封装）。要在对话里出图，请用下方「动态图表渲染」在浏览器中绘制。

3. **操作 Word 文件**
   - *"帮我读取 D:\test\report.docx 的前两段内容"*

4. **动态图表渲染**
   - *"根据这周一到周五的气温（15, 18, 20, 16, 19），用 Echarts 帮我画一个折线图"*
   - *(AI 会生成带有 `<html_canvas>` 标签的代码，插件会自动解析并在下方以网页形式渲染图表)*

---

## 🔒 安全说明

### 威胁模型（请务必阅读）

- 本服务设计为**本机助手**：默认从 `http://127.0.0.1:8765` 起尝试绑定（可在 `appsettings` 中配置 `WebSocket:Port` 与 `PortFallbackCount`），信任边界是「能访问该端口的本机进程」。
- **不要将 8765 端口无防护地暴露到公网**（例如不经认证的 frp/ngrok 转发）。若必须用非回环地址监听，请在 **`user-config.json` 中配置强随机的 `webSocketAuthToken`**，并在 Chrome 扩展选项页填写相同密钥；否则启动时会拒绝绑定（可使用 `--allow-public-bind` 显式承担风险，**强烈不推荐**）。
- **HTTP API**（`/api/*`）：未在 `user-config.json` 中配置 `webSocketAuthToken` 时**仅允许本机 loopback** 访问；配置后须携带请求头 `X-OfficeCopilot-Token` 或 `Authorization: Bearer`，与 WebSocket 查询参数 `token` 使用同一密钥。
- **外部 MCP**：配置的启动命令等价于在本机执行对应进程，请勿添加不可信来源的配置。
- **CLI 插件**：使用 `cmd /c`；在「RunEverything」模式下可执行任意命令，其它模式依赖人工确认，误点「允许」仍有风险。

### 其它机制

- **Origin 校验**：WebSocket 与 CORS 按 `WebSocket:AllowedOrigins` 前缀匹配，并允许常见 `chrome-extension://` / 本机调试来源。
- **命令与脚本策略**：服务端内置 `SecurityFilter`，可按端配置白名单、`AskEverytime` 或 `RunEverything`（见扩展「MCP」页说明）。
- **测试连接防 SSRF**：设置页「测试连接」默认禁止指向 localhost、内网与常见元数据地址；可在选项中勾选「允许内网/回环测试」写入 `allowPrivateEndpointTests`（会降低防护，仅供调试）。

## 🛠️ 二次开发

### 从本仓库克隆后
- 在 `backend` 目录下将 `appsettings.Example.json` 复制为 `appsettings.json`，按需填写 API Key 等（或通过插件设置面板配置，使用 `user-config.json`）。

### 服务端开发
项目基于 .NET 10 / C# 编写。工程为多目标框架：`net10.0`（默认，便于 `dotnet run` 控制台调试）与 `net10.0-windows`（含 Windows 托盘）。
```bash
cd backend
dotnet build
dotnet run
```
- **调试日志网页（本机）**：服务启动后可在浏览器打开 `http://127.0.0.1:8765/debug/logs.html`（仅本机 loopback 可调 `GET /api/debug/log-files`、`/api/debug/log-tail`），便于复制链接与他人对照日志。
- **Windows 托盘模式**：在 Windows 上执行 `dotnet run -f net10.0-windows -- --tray`，或使用 Visual Studio / Rider 启动配置 **OfficeCopilotTray**。托盘菜单「设置」会尝试用 Chrome 打开 `chrome-extension://<扩展ID>/options.html`；扩展 ID 请在 `user-config.json` 中配置 `chromeExtensionId`，或设置环境变量 `OFFICECOPILOT_CHROME_EXTENSION_ID`（在 `chrome://extensions` 开启开发者模式后从列表复制）。

### 插件开发
Chrome 插件基于 Manifest V3 编写，主代码在 `sidepanel.html` / `sidepanel.js`。修改后只需在 Chrome 扩展管理页点击“刷新”图标即可生效。