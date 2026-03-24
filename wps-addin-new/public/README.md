# `public/` 与任务窗格主界面

**产品主界面**：`wpsjs debug` / Vite 开发时实际使用的是 **Vue 路由**下的 [`src/components/TaskPane.vue`](../src/components/TaskPane.vue) + [`src/composables/useCopilot.js`](../src/composables/useCopilot.js)。

本目录下的 `taskpane.html` / `taskpane.js` 等**不与 Vue 版功能逐项同步**；请仅将其视为 **RPC 同步脚本**（如 `npm run sync-public-ppt-rpc`）或历史/备用入口。修改 WPS 侧宿主能力时，以 `useCopilot.js` 为准，再按需同步 `public/taskpane.js`。
