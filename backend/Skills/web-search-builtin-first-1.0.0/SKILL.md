---
name: web_search_builtin_first
title: 网络搜索（优先内置检索）
version: 1.0.0
description: 查新闻、百科、项目近况、实时事实等「网上有什么」类需求：优先依赖当前对话模型已开启的百炼「联网搜索」(enable_search / DashScope)，用自然语言直接组织答案，不要用 run_command 拉 curl、不要为替代检索而新开浏览器页再抠 SERP。检索：联网 搜索 资讯 实时 enable_search 百炼 内置检索 避免 curl 浏览器 替代搜索。
metadata: {"clawdbot":{"emoji":"🌐","os":["linux","darwin","win32"]}}
---

## OpenWorkmate（本仓库）— 何时用本技能

- 用户要的是 **公开网络信息**（新闻、文档、版本说明、竞品、概念解释等），且 **不要求** 你操作他「正在看的这一页」DOM。
- 已在 **Chrome 设置 → 对话大模型** 中为当前模型勾选 **百炼「联网搜索」**（`enable_search`）时：服务端会在 system 里附带「少用浏览器/curl 当搜索引擎」的说明；本技能与之一致，**进一步强调**行为优先级。

## 默认策略（优先内置检索）

1. **先直接作答**：把用户问题写清楚，让 **模型自带的联网检索**（`enable_search`）在当轮生成里补全时效信息；在正文中归纳来源类型即可（如「据公开报道」「项目 Release 页显示」），**不必**先 `run_command` 去 `curl` 搜索引擎 HTML 再 `findstr`。
2. **不要轻易**：`run_command` 打开系统默认浏览器、用 `run_custom_javascript_in_page` 里 `window.open` 仅为了搜关键词、或反复用 `page_agent` / `run_custom_javascript_in_page` 抠搜索结果页当「唯一信源」——除非用户明确要求或下文「例外」成立。
3. **信息够了就停**：内置检索已给出要点时，不要为了「显得做了工具调用」再叠一层本地爬页。

## 例外（仍可用 Browser / CLI）

- 用户明确要 **当前活动标签页** 里的内容（摘录、对比本页与摘要、操作页面元素）→ 用 **Browser** 的 `page_agent` / `run_custom_javascript_in_page` 等，与本技能不冲突。
- 用户给的 **具体 URL** 且需要 **页面结构/DOM**（表格、登录后才有的内容、站内导航）而不仅是「搜一下某某」→ 再考虑读页或命令行抓取，并说明局限。
- **当前模型未配置** `enable_search`、或用户明确要「用我电脑上的命令行/浏览器打开某站」→ 按用户意图选用工具，并在回复里说明依据来源。

## 与渐进式技能链的配合

- 若本轮任务以 **网络事实** 为主：可用 `search_available_skills` 命中本技能 → `select_skill_for_turn`（`skillId` = **`web_search_builtin_first`**）→ `load_user_skill_instructions` 后再作答；**不必**为「能搜到」而额外 `activate_tools` 拉 Browser 全家桶。
- 若任务同时需要 **落盘 Word** 等：先按本技能整理事实，再按 `word_cn_default_formal` / `word-docx` 等技能调用 Word 工具；勿把 curl 抓到的 HTML 整坨塞进 **`word_document_create` 的 `paragraphs`（`string[]`）的某一个元素**。

## 输出与诚实性

- 内置检索 **未返回** 或明显不足时：**如实说明**「当前检索未覆盖某点」，可建议用户换关键词、补充链接，或在确认后使用读页/CLI；不要编造细节。
- 工具失败须可转述（见仓库 error-visibility 约定）。
