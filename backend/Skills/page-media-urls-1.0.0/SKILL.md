---
name: page-media-urls
version: 1.0.0
description: 从当前网页收集图片或视频 URL 时，优先用 Browser 工具；必要时自定义脚本。检索：图片 URL 视频 媒体 当前标签页
---

## 流程要点

1. 用户切到目标标签；可用 **`page_agent`** `{"op":"observe"}` 了解页内可交互区域。
2. 媒体 URL 往往在 DOM 属性或 JSON 中，优先 **`run_custom_javascript_in_page`**（`return JSON.stringify(...)`），或按附件脚本 `references/collect-image-urls.js` / `collect-video-urls.js` 全文粘贴为 `scriptCode`。
3. 将返回的**纯 JS 全文**（无 markdown 围栏）作为 **`run_custom_javascript_in_page`** 的 **`scriptCode`**。
