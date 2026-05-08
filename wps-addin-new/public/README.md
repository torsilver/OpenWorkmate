# `public/` 静态资源

本目录由 **Vite** 将文件**原样**输出到站点根（与根目录 [`index.html`](../index.html) 同级），供 **Vue 应用**（含路由 **`#/taskpane`**）与功能区等引用。

## 主要内容

- **`ribbon.xml`**：功能区定义（与 [`src/components/ribbon.js`](../src/components/ribbon.js) 配合）。
- **`OpenWorkmate-theme-boot.js`**、**`chat-themes.css`**、**`libs/*`**：主题与代码高亮等（根 `index.html` 引用）。
- **`local-service-resolve.js`**：本地服务基址解析（若入口脚本引用）。
- **`images/`**、**`fonts/`**、**`favicon.ico`** 等其它静态资源。

## 任务窗格实现位置

任务窗格的 **UI、WebSocket、宿主 RPC** 仅在 **Vue 栈**维护：

- [`src/components/TaskPane.vue`](../src/components/TaskPane.vue)
- [`src/composables/useOpenWorkmate.js`](../src/composables/useOpenWorkmate.js)

历史上曾存在独立的 `taskpane.html` / `taskpane.js` / `taskpane.css` 静态任务窗格，已与 **`sync-public-ppt-rpc`** 同步脚本一并移除，避免与 Vue 双轨混淆。
