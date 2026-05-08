---
name: page_media_url_collect
title: 网页媒体链接摘录
version: 1.1.0
description: Chrome 当前标签页：预置两段独立 JS——仅图片（img/og/icon/style）与仅视频流（video/audio/外链/JSON-LD 启发式）。按任务加载其一或依次执行；不下载、失败如实告知。检索：图片链接 视频 m3u8 媒体 URL。
metadata: {"clawdbot":{"emoji":"🖼️","os":["linux","darwin","win32"]}}
---

## OpenWorkmate（本仓库）

- **设计**：**图片**与**视频**各一份页内脚本，便于**分别调参、单独迭代**，互不牵累。模型按用户意图选用 **`references/collect-image-urls.js`** 和/或 **`references/collect-video-urls.js`**，不要混成一份大杂烩后再改。
- **何时用哪段**  
  - 只要图 / 封面 / 图标 / 背景图 URL → **仅加载并执行** `collect-image-urls.js`。  
  - 只要视频、音频流、下载链、HLS/DASH 线索 → **仅加载并执行** `collect-video-urls.js`。  
  - 用户要「图 + 视频都要」→ **先执行其一**，再对**同一标签页**执行其二（中间可 `scroll_to_bottom` 一次）；向用户说明两次结果可能重复统计同一页面。
- **执行步骤**（每段脚本相同）  
  1. 用户切到目标标签；可选 **`run_builtin_page_script`** `scroll_to_bottom` 再读。  
  2. **`load_user_skill_instructions`**，`skillId` = **`page_media_url_collect`**，`relativeResourcePath` = 上列 **其一**（如 `references/collect-image-urls.js`）。  
  3. 将返回的**纯 JS 全文**（无 markdown 围栏）作为 **`run_custom_javascript_in_page`** 的 **`scriptCode`**。  
  4. 解析返回的 **JSON 字符串**：图片脚本含 **`kind":"images"`** 与 **`images`**；视频脚本含 **`kind":"videos"`** 与 **`videos`**；**`notes`** 必展示给用户要点。
- **前置与失败**：须 **Allow User Scripts**；HITL 被拒、**`[Error]`**、空结果等 **原样转述**，禁止编造 URL（与 error-visibility 一致）。

## 能力与硬边界

- **图片脚本**：`img`/`picture`/`image`、`og/twitter` 图、`link rel=icon` 等、`srcset`/`data-srcset`、内联 `style` 里 `url()` 且扩展名像图片。  
- **视频脚本**：`video`/`source`、`audio`、`a[href]` 中疑似媒体扩展名、`object`/`embed`、**粗**扫 `application/ld+json` 中的 `contentUrl`/`embedUrl`。**不**解析 DRM、**不**保证 m3u8 可离线播放。  
- **不能做的**：不走 CLI 批量下载；不保证跨域可 fetch；Shadow DOM/跨域 iframe 内资源可能扫不到——**如实说明**。

## 与其它技能的关系

- **`web_chat_conclusion_summary`**：对话文本。  
- **`CaptureFullPageScreenshot`**：像素备份。

## 脚本维护

- **图片迭代**只改 [`references/collect-image-urls.js`](references/collect-image-urls.js)。  
- **视频迭代**只改 [`references/collect-video-urls.js`](references/collect-video-urls.js)。  
- 单文件须保持可被 **`scriptCode`** 整段容纳（小于扩展脚本长度上限）。
