---
name: web-chat-conclusion-summary
version: 1.0.0
description: 用户在外站网页（免费版 ChatGPT/Claude/Gemini 等）与 AI 长聊后，用 OpenWorkmate 总结：须从浏览器当前标签页读取信息（Browser 插件 `page_agent` 先 observe，必要时 `run_custom_javascript_in_page`）。检索：外站 总结 网页 AI 对话 读页面 当前标签页 摘录。
---

## 何时使用

- 用户明确要**总结当前浏览器标签页里**与某外站 AI 的长对话，且上下文不足以直接作答。

## 能力前提

- 读页依赖 **`Browser`** 插件的 **`page_agent`** / **`run_custom_javascript_in_page`**，由扩展在**当前活动标签页**注入执行。请确认会话为 **Chrome 端**且扩展已连接；可能触发安全/HITL 确认，需引导用户**同意**后再继续。

## 建议步骤

1. **`page_agent`**，`requestJson` = `{"op":"observe"}` — 取得 `title`、`url` 与带 `ref` 的 `nodes` 列表。
2. 若 `observe` 的节点列表不足以概括对话正文，在设置已开启 **Allow User Scripts** 且策略允许时，用 **`run_custom_javascript_in_page`** 写**带 `return`** 的短脚本针对该页 DOM 抽取对话容器（可能需用户 HITL 确认）。
3. 将摘录整理为用户可读总结；不要编造未从页面得到的内容。
