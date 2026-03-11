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
   - 看到提示 `Now listening on: http://localhost:8765` 表示服务端启动成功。
   - 配置（如 API Key）可以直接在浏览器的设置面板中完成，无需修改 JSON 文件。

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

3. **操作 Word 文件**
   - *"帮我读取 D:\test\report.docx 的前两段内容"*

4. **动态图表渲染**
   - *"根据这周一到周五的气温（15, 18, 20, 16, 19），用 Echarts 帮我画一个折线图"*
   - *(AI 会生成带有 `<html_canvas>` 标签的代码，插件会自动解析并在下方以网页形式渲染图表)*

---

## 🔒 安全说明

- **Origin 校验**：服务端 WebSocket 默认只接受来自 Chrome 插件的连接，防止恶意网页利用。
- **命令白名单**：服务端内置 `SecurityFilter`，默认仅允许执行如 `dir`, `echo`, `ping` 等安全命令。如果大模型尝试执行 `del`、`format` 等危险操作，将被直接拦截。

## 🛠️ 二次开发

### 从本仓库克隆后
- 在 `backend` 目录下将 `appsettings.Example.json` 复制为 `appsettings.json`，按需填写 API Key 等（或通过插件设置面板配置，使用 `user-config.json`）。
- 若使用销售数据库 MCP，参见 `sales-db-mcp/README.md` 配置连接串。

### 服务端开发
项目基于 .NET 8 / C# 编写。
```bash
cd backend
dotnet build
dotnet run
```

### 插件开发
Chrome 插件基于 Manifest V3 编写，主代码在 `sidepanel.html` / `sidepanel.js`。修改后只需在 Chrome 扩展管理页点击“刷新”图标即可生效。