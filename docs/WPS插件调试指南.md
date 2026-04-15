# WPS 加载项（wps-addin-new）调试指南

本文汇总 **金山 WPS 官方/文档站公开的加载项调试说明**，并结合本仓库 **[wps-addin-new/](../wps-addin-new/)** 的目录与依赖，给出可操作的调试步骤。官方站点与 CDN 文档会随版本调整，若链接失效请以 [WPS 开放平台](https://open.wps.cn/) 当前导航为准。

---

## 1. 官方文档从哪里看

| 资源 | 说明 |
|------|------|
| [WPS 开放平台](https://open.wps.cn/) | 对外产品与文档入口；加载项、JSAPI 等以站内检索为准。 |
| [加载项历史文档（wpsload）](https://open.wps.cn/previous/docs/client/wpsload) | 开放平台「往期」中的客户端加载项说明，可作入口书签。 |
| [WPS 加载项开发说明（文档镜像）](https://qn.cache.wpscdn.cn/encs/doc/office_v8/topics/WPS%20%E5%8A%A0%E8%BD%BD%E9%A1%B9%E5%BC%80%E5%8F%91/WPS%20%E5%8A%A0%E8%BD%BD%E9%A1%B9%E5%BC%80%E5%8F%91%E8%AF%B4%E6%98%8E.html) | 金山 CDN 上的 **WPS 开发人员参考** 系列；其中 **「调试」** 一节对加载项调试方式有明确描述（与下文第 2 节对应）。 |
| [WPS 加载项概述（文档镜像）](https://qn.cache.wpscdn.cn/encs/doc/office_v8/topics/WPS%20%E5%8A%A0%E8%BD%BD%E9%A1%B9%E5%BC%80%E5%8F%91/index.html) | 说明加载项基于 Web 技术、与 WPS 进程内 Chromium 内核的关系及任务窗格等形态。 |

> 说明：`qn.cache.wpscdn.cn/encs/doc/...` 为常见文档镜像路径，**文档版本号（如 office_v8）可能随官方更新而变化**；若 404，请在开放平台或站内搜索「WPS 加载项 开发说明」获取最新页。

---

## 2. 官方对「调试」的表述（摘要）

以下内容摘自官方文档镜像《WPS 加载项开发说明》中的 **「调试」** 小节要点，便于与浏览器开发对照：

1. **调试对象**：对加载项中的 **某一个网页** 单独调试；会弹出 **独立调试器窗口**，除此外与网页调试基本一致。
2. **Console**：可在调试器 Console 中查看 API 属性、调用 API 方法。
3. **快捷键**：调试 **自动生成的 `index.html`** 时，可使用 **Alt + F12** 打开调试器。
4. **同步阻塞**：调试过程中若出现 **alert 或其它同步弹框**，需 **先关闭弹框** 才能继续调试。

与本项目相关：

- 本仓库 **规范实现** 在 **Vue + Vite**（`src/`、`#/taskpane`），不是仅依赖自动生成的根 `index.html`；实际调试时应在 **当前正在显示的任务窗格 / 对话框页面** 上打开开发者工具（见下文第 4 节）。
- 宿主 API 集中在 **`window.wps`**（文档中常写作 `wps`），任务窗格内脚本需结合 **任务窗格** 与 **WPS 应用程序** 两端的上下文排查。

---

## 3. 本仓库推荐调试方式：`wpsjs debug`

仓库约定见 [.cursor/rules/wps-plugin-dev.mdc](../.cursor/rules/wps-plugin-dev.mdc)：**WPS 侧验证应以 `wpsjs debug` 为准**，不要仅依赖 `npm run dev` / `npm run build`（那只是 Web 构建，**不等于**在 WPS 宿主内可运行、可调试）。

### 3.1 环境准备

1. 安装 **WPS 客户端**（与加载项目标版本一致为佳：文字 / 表格 / 演示）。
2. 安装 **Node.js**，在仓库目录安装依赖：

```bash
cd wps-addin-new
npm install
```

3. 全局或本地使用 **`wpsjs`**（本项目 `devDependencies` 已包含 `wpsjs`，通常通过 `npx` 调用即可）。

### 3.2 启动调试

在 **`wps-addin-new` 根目录** 执行：

```bash
npx wpsjs debug
```

或使用全局安装的 `wpsjs`：

```bash
wpsjs debug
```

常见行为（以官方工具实际输出为准）：

- 拉起本地开发服务，并由 **WPS 进程加载当前加载项**；
- 支持开发期 **热更新**（页面变更后刷新或自动刷新，视 `wpsjs` 版本与模板而定）。

若命令异常，可按官方与社区常见建议 **升级工具**：例如 `npm update -g wpsjs` 或更新项目内 `wpsjs` 依赖版本后重试。

### 3.3 与本项目结构的对应关系

| 路径/文件 | 说明 |
|-----------|------|
| [wps-addin-new/manifest.xml](../wps-addin-new/manifest.xml) | 加载项清单（`JsPlugin`）。 |
| [wps-addin-new/public/ribbon.xml](../wps-addin-new/public/ribbon.xml) + `src/components/ribbon.js` | 功能区与打开任务窗格等入口。 |
| [wps-addin-new/src/](../wps-addin-new/src/) | **Vue 主线**：`TaskPane.vue`、`composables/useCopilot.js`（路由 `#/taskpane`）等。 |
| [wps-addin-new/public/README.md](../wps-addin-new/public/README.md) | `public/` 仅静态资源与 ribbon 等；**无**独立静态任务窗格页面。 |

### 3.1 会话上下文与后端工具可见性（`wpsHostKind`）

Vue 侧在 WebSocket **`set_context`** 中携带 **`wpsHostKind`**（与 [`wps-addin-new/src/wps-rpc/hostKind.js`](../wps-addin-new/src/wps-rpc/hostKind.js) 同源），供后端注入 system 与 **`SessionManager`** 存储。后端在 **`clientType === wps`** 时用它驱动 **`ClientTypeToolFilter`**：

- 规范化后为 **`word` / `et` / `wpp`**：`CurrentDocument` 具名工具与对应 **`office-word` / `office-excel` / `office-powerpoint`** 子集一致（含 `current_run_document_script` / `current_run_custom_document_script`）。
- **`unknown` / `none` / 尚未上报（null）`**：**不收紧**，模型仍可见 Word+Excel+PPT 并集（与首轮未 `set_context` 行为一致）。

动态工具 **`search_available_tools` / `activate_tools`** 的索引与校验同样走上述过滤；单轮快照字段为 **`DynamicToolingTurnState.WpsHostKindForTools`**。详见 [`docs/应用内AI插件列表.md`](./应用内AI插件列表.md) §三、[`docs/动态工具与技能选择实现说明.md`](./动态工具与技能选择实现说明.md)。

---

## 4. 开发者工具（DevTools）在任务窗格里的用法

官方文档强调：**对其中一个网页单独调试**。本项目的 **Office Copilot 任务窗格** 是内嵌 WebView，与主窗口可能 **不是同一个调试目标**。

实践建议：

1. **先聚焦**到任务窗格（点击任务窗格区域）。
2. 尝试 **F12** 或文档所述 **Alt + F12**（以你本机 WPS 版本菜单/快捷键为准）。
3. 若主窗口与任务窗格 **分离调试**，请在 **对应网页** 上打开 DevTools，再看 **Console / Network**（例如本机 `Office Copilot Server` 的 WebSocket、HTTP）。

与 **本机后端** 联调时：

- 确保 [backend](../backend/) 已启动，扩展/加载项能通过 [local-service-resolve](../chrome-extension/local-service-resolve.js) 同类逻辑解析到端口（详见 [wps-addin-new/src/utils/tasklyLocalService.js](../wps-addin-new/src/utils/tasklyLocalService.js)）。
- WebSocket / REST 错误请同时看 **WPS 窗格 Console** 与 **服务端日志**。

---

## 5. 可选：通过客户端配置开启 Web 调试能力（进阶）

部分社区与历史文档提到：在 WPS 安装目录下的配置中开启 JS 加载项或 Web 调试相关开关（例如 `office6/cfgs/` 下 `oem.ini` 的 `[Support]` 中 `JsApiPlugin`、`JsApiShowWebDebugger` 等）。**不同发行版、版本（尤其个人版安全策略更新后）是否仍允许改配置文件，以官方最新说明为准**；若与 `wpsjs debug` 冲突，优先以 **官方工具链 + 当前 WPS 版本说明** 为准。

遇到「无法加载 / 无法调试」时，可补充查阅：

- [WPS 官方社区](https://bbs.wps.cn/) 中与 **加载项、wpsjs、调试** 相关的置顶或公告（例如版本策略变更说明）。

---

## 6. 与「仅 Vite」开发的区别（避免踩坑）

| 命令 | 用途 | 是否等价于 WPS 内调试 |
|------|------|------------------------|
| `npm run dev`（Vite，默认端口见 [package.json](../wps-addin-new/package.json)） | 浏览器里跑 Vue SPA | **否**：无 `wps`、`Application` 等宿主对象。 |
| `npm run build` | 生产构建 | **否**：仅验证前端能否编译。 |
| `npx wpsjs debug` | 在 WPS 中加载加载项并开发调试 | **是**（本仓库推荐的 WPS 验证方式）。 |

---

## 7. 参考与对照（非业务实现目录）

- [HelloWps/](../HelloWps/)：官方模板/示例，仅作 API 与工程结构对照；**业务与调试文档以 `wps-addin-new` 与本篇为准**（见 `.cursor/rules/wps-plugin-dev.mdc`）。

---

## 8. 文档维护

- 若 WPS 官方更换域名或文档路径，请更新本文 **§1 链接** 与必要时 **§2 表述**。
- 若仓库内 `wpsjs` 脚本或入口变更，请同步更新 **§3** 与 [wps-addin-new/README.md](../wps-addin-new/README.md)。
- 若 **`set_context` / `wpsHostKind`** 或后端 `ClientTypeToolFilter` 规则变更，请同步 **§3.1** 与上文两篇后端文档。
