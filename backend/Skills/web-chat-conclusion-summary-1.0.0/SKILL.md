---
name: web_chat_conclusion_summary
title: 网页对话结论总结
version: 1.1.0
description: 用户在外站网页（免费版 ChatGPT/Claude/Gemini 等）与 AI 长聊后，用 Taskly 总结：须从浏览器当前标签页读取 DOM 文本（run_builtin_page_script：chat_page_tail_glance、get_visible_text 等）。检索：外站 总结 网页 AI 对话 读页面 当前标签页 摘录。
metadata: {"clawdbot":{"emoji":"📝","os":["linux","darwin","win32"]}}
---

## Taskly（本仓库）— 场景说明

- **典型场景**：用户先在**别的网站**的 AI 对话框里聊了很久（常见为免费网页版），再在 **Taskly（Chrome 扩展侧栏）**里请你做**最终总结**。此时事实来源是 **Chrome 当前活动标签页里的页面内容**，**不是** Taskly 侧栏里你与用户新开的这几条消息（除非用户明确说「就总结我们这边刚聊的」）。
- **能力前提**：读页依赖 **`Browser`** 插件的 **`run_builtin_page_script`** / **`run_custom_javascript_in_page`**，由扩展在**当前活动标签页**注入执行。请确认会话为 **Chrome 端**且扩展已连接；执行读页脚本可能触发安全/HITL 确认，需引导用户**同意**后再继续。
- **用户操作**：总结前应请用户**切换到外站 AI 对话所在标签**，并尽量滚到对话底部（懒加载消息常见）；必要时由你用脚本 **`scroll_to_bottom`** 再读。

## 推荐读页顺序（从外站页面取原文再总结）

1. **`run_builtin_page_script`**，`scriptId` = **`get_page_title`**，`paramsJson` = `{}` — 确认标题/是否切错标签。
2. **`scroll_to_bottom`**（`scriptId` = `scroll_to_bottom`）— 触发懒加载、尽量载入较晚消息。
3. **先试泛化对话摘录**：`scriptId` = **`chat_page_tail_glance`**，`paramsJson` 如 `{"maxTailChars":32000}` — 按常见 `data-message-author-role`、`model-response`、`role=article` 等**启发式**取**偏末尾**正文；返回里自带说明：不同站点 DOM 差异大，**不保证**等于「整段对话」或「最后一轮」语义。
4. **若需更长上下文或 glance 失败**：`scriptId` = **`get_visible_text`**，例如：
   - 只要近期：`{"maxLength":120000,"truncateMode":"tail"}`
   - 长对话兼顾首尾：`{"maxLength":200000,"truncateMode":"both"}`
   - 仍过长：分步 `scroll_to_top` + `get_visible_text`（`truncateMode":"head"`）与再 `scroll_to_bottom` + `tail` 两段读取，在总结中合并说明「前段/后段来源」。
5. **站点结构特殊**：当预置脚本取到的全是导航/侧栏等噪音时，在设置已开启 **Allow User Scripts** 且策略允许时，用 **`run_custom_javascript_in_page`** 写**带 `return`** 的短脚本，针对该页 DOM 抽取对话容器（可能需用户 HITL 确认）；**不要**用自定义脚本去做与本任务无关的高危操作。
6. **读到的字符串**即你写总结的**唯一依据**；不得把外站未出现在返回文本里的内容写成事实。工具返回 **`失败：`** / **`[Error]`** 等须**转述用户**（error-visibility）。

## 总结输出（在成功取到页面文本之后）

- 先确认一句：总结依据为**当前标签页**在某某标题/URL 下抓取到的文本（可从 `get_page_outline` 或用户口述对齐）。
- 按用户要的风格输出（极简 / 纪要 / 只要待办），结构见下；**不得**把 Taskly 推理流当外站结论。
- **极简**：一段话结论 + 3～5 条要点。
- **纪要**：背景 / 结论 / 决策与理由 / **待办** / **风险与未决** / 可选「下一步」。
- **可执行项**：分「立即可做 / 需用户确认 / 需外部信息」。

## 可选后续（非读页核心）

- **`save_memory`**：用户要求长期记住本总结且已配置 Embedding 时；失败须转述原因。
- **`word_document_create`**：用户要可分享稿时落盘（`paragraphs` 为 **`string[]`**）；正式中文排版可叠加 **`word_cn_default_formal`**。
- **`compact_conversation`**：仅用于 **Taskly 本会话**上下文过长、且用户已拿到总结后要换话题时；**不能**代替从网页读外站对话。
- **`generate_user_skill`**：用户想把「我常要的读页+总结结构」固化时。

## 不要做的事

- **不要**在未调用读页工具（或调用失败）时，假装已「读完」外站全文并写总结。
- **不要**把本技能当成绕过外站付费/登录的手段；是否合规以**各平台服务条款**及用户授权为准，模型可提示用户自行判断。
- **不要**用 `compact_conversation` 从网页拉取外站历史——它只处理 **Taskly 会话**内的消息列表。

## 与其它技能的关系

- **`Pdf / Pdf`**：若用户给的是 PDF 导出稿而非 live 网页，改走 PDF 技能。
- **`CaptureFullPageScreenshot`**：整页截图归档用；**文字总结仍以抽取文本为主**，截图+OCR 仅作补充。
