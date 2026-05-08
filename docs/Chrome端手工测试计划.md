# Chrome 端功能手工测试计划

> **范围**：仅针对 **Chrome 扩展**（`chrome-extension/`）+ 本机 **Open Workmate Server**；测试素材与操作在 Chrome 内完成。  
> **不包含**：Cursor/VSCode 侧自行配置的 MCP、Office/WPS 任务窗格专属能力。  
> **内置工具定义**：以运行时 **ToolRegistry**（`ChatService.RebuildRuntimeAsync`）为准。**GET** `/api/tools/builtin`（`Program.cs`）当前返回 **18** 条，**仍缺少** `Pdf`、`Context`、`Subagent`、`CrossAgentTask`、`ScheduledTask` 共 **5** 项（详见 [应用内AI插件列表.md](应用内AI插件列表.md) §五）。手工核对插件开关请以该文档 **§1.1**（`disabledBuiltInPlugins` 小写 id）与 **§1.3**（各插件函数数）为准。  
> **Chrome 的 `clientType` 为 `chrome` 时，不暴露 `CurrentDocument` 插件**（其余已注册且未禁用的插件均可暴露）。详见 `backend/Services/ClientTypeToolFilter.cs`。  
> **内置插件数量**：后端 `RebuildRuntimeAsync` 在「未禁用」前提下**无条件注册 22 个**内置插件名 + 可选 **`Memory`**（Embedding 已配置）⇒ 注册侧最多 **23** 个；Chrome 端裁剪掉 **`CurrentDocument`** 后模型可见 **21** 个 + **`Memory`** ⇒ 最多 **22**。与 [应用内AI插件列表.md](应用内AI插件列表.md) §「与代码核对」一致。

**通过标准（每条用例）**：行为符合描述；失败时 **HTTP 4xx/5xx 或工具返回中带明确原因**（见项目错误可见性约定），侧栏/选项页能展示服务端 `message`。

> **Playwright 自动化**：仓库 `e2e/` 已覆盖本节部分壳层与辅助页；**其余仍需手工**的条目汇总见 [Chrome端手工测试-Playwright无法覆盖清单.md](Chrome端手工测试-Playwright无法覆盖清单.md)。

---

## 零、测试前环境检查


| 序号  | 检查项                    | 说明                                                                                                                                   |
| --- | ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| Z1  | 后端已启动                  | 扩展能连上 API（侧栏状态或选项页「连接」正常）                                                                                                            |
| Z2  | `chrome-extension` 已加载 | `chrome://extensions` 中已启用本扩展，版本与仓库一致                                                                                                |
| Z3  | 访问密钥 / Token           | 与 `user-config` 或选项页配置一致，请求不被 401                                                                                                    |
| Z4  | 模型与 Embedding 等        | 按需：Memory 需 Embedding；联网问答需在百炼模型上开启 **enable_search**；OCR/STT 需选项页中对应配置；测 §3.2a **Pdf** 时确认未在 `disabledBuiltInPlugins` 中禁用 `**pdf`** |
| Z5  | 下载目录                   | File 保存截图、Word/Excel/Ppt/Pdf 写本地文件时，路径解析以本机「下载」或配置为准；相对文件名同 §3 约定                                                                    |


---

## 一、Chrome 扩展壳与通用交互

### 1.1 侧栏与页面


| 序号  | 场景    | 操作                                              | 预期                                                                                                    |
| --- | ----- | ----------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| C1  | 打开侧栏  | 点击扩展图标或使用 Side Panel                            | 出现 `sidepanel.html` 对话界面                                                                              |
| C2  | 当前页标签 | 打开任意网页，看侧栏 **header** 副标题 `#current-page-label` | 与当前激活标签标题或 URL 一致（或合理占位）                                                                              |
| C3  | 设置入口  | 点击 ⚙️ 或扩展「选项」                                   | 打开 `options.html`（`manifest.json` 的 `options_page`）                                                   |
| C4  | 新会话   | 点击 💬 新对话                                       | 上下文清空，无串会话                                                                                            |
| C5  | 停止生成  | 长回复中途点「停止」                                      | 流式停止，无长时间卡死                                                                                           |
| C6  | 附件    | 点回形针，选本地文件                                      | 侧栏 `**accept="image/*"`**，仅图片；预览后出现；发送后用户消息含 `attachment:` 引用（通用文件请用下载目录路径 + File/Pdf 等工具，勿依赖侧栏非图片附件） |


### 1.2 语音输入（扩展内，非 `transcribe_audio` 工具）


| 序号  | 场景        | 操作         | 预期                         |
| --- | --------- | ---------- | -------------------------- |
| V1  | 正常收音      | 点麦克风，说话后停止 | 文本进入输入框或作为用户消息（依赖实现）       |
| V2  | 拒绝麦克风     | 浏览器拒绝麦克风权限 | 侧栏出现可理解的错误与「打开权限设置」类引导（若有） |
| V3  | 未配置百炼 ASR | 清空/错误 Key  | 明确错误提示，非静默失败               |


### 1.3 会议监听（Chrome 侧栏）


| 序号  | 场景    | 操作                | 预期                                                                                                  |
| --- | ----- | ----------------- | --------------------------------------------------------------------------------------------------- |
| M0  | 开始监听  | 点会议监听，允许麦克风       | 生成 `meeting_…` 会话 ID，系统消息中可复制 **sessionId**；并 **自动新开** `meeting-live.html` 标签页（大屏逐条刷新，需保持侧栏连接以继续推流） |
| M1  | 实时转写  | 说话数句后看侧栏实录区或实录标签页 | 有增量转写（停顿切片，具体以后端/ASR 为准）                                                                            |
| M2  | 结束    | 点「结束并总结」          | 结束录音与转写；可配合 **MeetingTranscript**、对话中按 sessionId 总结等用例                                              |
| M3  | 下载与说明 | 监听中点「下载实录」「导出说明」  | 能下载实录 HTML；导出说明可读（引导在对话中使用 sessionId 等）                                                             |


### 1.4 `@` 模式与内置插件列表


| 序号  | 场景       | 操作                     | 预期                                                          |
| --- | -------- | ---------------------- | ----------------------------------------------------------- |
| A1  | 加载 Tools | 在输入框触发 `@`（或项目约定的唤起方式） | 列出 Tools + Skills；**运行时**以 WS 下发为准。`/api/tools/builtin` 仅为 **18 条**设置页子集且缺 Pdf 等 5 项（文首说明），与 `@` 全量列表不必逐项相同 |
| A2  | 指定工具     | 选择某一内置插件名发送            | 对话中带工具约束，模型优先使用该方向能力                                        |


### 1.5 计划 UI


| 序号  | 场景          | 操作                         | 预期                                                                                        |
| --- | ----------- | -------------------------- | ----------------------------------------------------------------------------------------- |
| P0  | 与 Plan 插件联动 | 在对话中让 AI 列计划（如「帮我制定分步计划…」） | 出现 planId、可自动打开 `plans.html?id=…`；在计划页「确认并开始执行」后与侧栏 `**execute_plan_step` / 当前计划绑定** 行为一致 |
| P1  | 侧栏计划条       | 看输入区上方「当前计划」与步骤指示          | 绑定计划时显示标题/步骤；点击标题可打开计划页；「✕」取消绑定（以当前 `sidepanel.html` 为准）                                  |


### 1.6 助手回复时间线（`msg--agent-timeline`）

> **实现位置**：`chrome-extension/sidepanel.js`（WS `type` → 时间线条目）。下列用例用于确认「一问一答」里助手侧**过程可视**与**顺序**与后端发帧一致；对照后端日志可搜 `WS send` / `tool_invocation` / `agent_phase`。


| 序号  | 场景       | 操作                                                       | 预期                                                                                                                                                                                               |
| --- | -------- | -------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| TL1 | 时间线块类型齐全 | 发一条会触发 **至少一次工具** 的请求（如 §3.1 B03 或「总结当前页并生成 Word 到下载目录」） | 在同一轮 `msg--round` 内出现可折叠块：**推理**（模型开启 thinking 时）、可选 **工具参数（生成中）**、**计划 / 意图**（`Plugin.tool` 文案）、**准备 / 状态**（慢 IO 等 `agent_status`）、绿色 **工具执行** 块、**处理工具结果**；随后若有下一轮模型输出则再出现 **推理** / **助手回复** 等 |
| TL2 | 推理与工具顺序  | 同上，展开各段扫一眼                                               | **推理**应在对应 **计划/意图** 与 **正在执行的工具块** 的上下文中可读；推理段不应被错误地只画在整轮最底部（已修复：新建推理段会插在「执行中」工具块之前）                                                                                                            |
| TL3 | 无整轮读秒条   | 流式进行中看侧栏 **header 下方、计划进度条上方**                           | **不应**再出现「处理中 · 已进行 N 秒」横条（已移除，避免焦虑）；进度感依赖时间线块与工具块状态即可                                                                                                                                           |
| TL4 | 工具块耗时    | 执行耗时 >1s 的工具（如 `run_builtin_page_script`）                        | 绿色工具块 summary 上可出现 **「已执行 Ns」** 或输出区「已耗时 Ns」（单工具粒度，保留）                                                                                                                                           |
| TL5 | HITL 确认  | 触发需确认的 `run_builtin_page_script` / `run_command` 等               | 出现 **顶栏遮罩**；遮罩内可有 **「等待确认 · 已 N 秒…」** 与超时说明。**不**要求时间线内出现「待确认」行（当前为刻意设计；若产品改为写入时间线需另改代码与本文）                                                                                                      |
| TL6 | 服务端提示    | 若某轮触发 `stream_warning`（如记忆/知识库类告警，依赖后端策略）                | 时间线内出现 **「服务端提示」** 折叠段（黄系样式），且相对前后推理/正文顺序与 WS 到达顺序一致                                                                                                                                             |
| TL7 | 最终正文     | 流结束                                                      | **助手回复**段含流式 Markdown；`stream_end` 后整轮折叠，最后一条助手结论仍可阅读                                                                                                                                            |


**与日志快速对照（可选）**：后端先发 `reasoning_chunk` → 再 `agent_phase`（intent）→ `agent_status`（可选）→ `tool_invocation_start` → … → `tool_invocation_end` → `agent_phase`（digest）→ 后续推理/正文；Chrome 时间线顺序应与此一致（HITL 的 `confirm_request` 在日志中有、UI 为遮罩不在时间线）。

### 1.7 Workspace（复杂回复外投）

侧栏在助手回复含 **较长 Markdown**、**fenced 的 mermaid 图**（Markdown 代码围栏内写 mermaid）或 `**<html_canvas>…</html_canvas>`** 等「复杂内容」时，会 **新建或聚焦** 扩展内页面 `**workspace.html`** 做渲染（见 `sidepanel.js` 中 `sendToWorkspace`）。较短且仅含 canvas 片段时也可能只在侧栏内 **「数据展示板」** 内嵌展示（`renderCanvas`）。


| 序号  | 场景           | 操作                                      | 预期                                                        |
| --- | ------------ | --------------------------------------- | --------------------------------------------------------- |
| WZ1 | 打开 Workspace | 让模型输出一段含 **mermaid** 流程图或较长结构化 Markdown | 出现 `workspace.html` 标签页且内容可读；侧栏可能对 canvas 类内容显示「已在展示板」类提示 |
| WZ2 | 与侧栏共存        | 上述过程中保持侧栏对话                             | 对话与时间线仍正常，无整页卡死                                           |


---

## 二、不应在 Chrome 暴露的插件（负向）


| 序号  | 场景              | 操作                                    | 预期                                             |
| --- | --------------- | ------------------------------------- | ---------------------------------------------- |
| N1  | CurrentDocument | 用户明确要求「读取当前 Word 文档选区」（不要在 Office 里测） | 模型**不应**假装成功；若误调工具应返回明确不可用说明（Chrome 无任务窗格 RPC） |
| N2  | 工具列表            | 在调试统计或开发者手段中查看当前会话可用工具（若可查看）          | 无 `CurrentDocument` 下工具                        |


---

## 三、按内置插件与工具（逐项可测）

下列每条**工具名**与后端插件上的 `**[ToolFunction("...")]`** 元数据一致。

- **路径约定**：未写盘符的**相对文件名**解析到当前 Windows 用户的下载目录（常为文件夹 `Downloads`，与后端 `OpenXmlHelpers.ResolvePath` 一致）。下文固定使用 `OpenWorkmate-excel-test.xlsx`、`OpenWorkmate-word-test.docx`、`OpenWorkmate-ppt-test.pptx`、`OpenWorkmate-img.png`，你可改名但同一轮请保持一致。
- **应核对工具名**：侧栏/调试统计/后端日志中是否出现该调用（用于统计**工具调用成功率**）。
- **表格与 Markdown**：下列表格用竖线分列；**话术列**内勿再写入与列分隔符相同的竖线字符（否则整行窜列），勿在单元格内写 Markdown 超链接语法（方括号 + 圆括号 URL）。需表示「竖线分段」时用文字 **U+007C** 或见 **§3.9 / §3.10**。
- 模型未选型时：先发「**请必须调用工具 xxx**」，或用 `@Excel` 等约束。

### 3.0 数据准备顺序（建议）

1. **E01** `excel_range_write` 生成或覆盖 `OpenWorkmate-excel-test.xlsx`。
2. **W01** `word_document_create` 生成 `OpenWorkmate-word-test.docx`。
3. **P01** `ppt_document_create` 生成 `OpenWorkmate-ppt-test.pptx`。
4. 下载目录放一张图片 `**OpenWorkmate-img.png`**，供 Word/Ppt 插图用例。
5. **PD01** `pdf_document_create` 生成 `OpenWorkmate-pdf-a.pdf`；**PD04**（见 §3.2a）再生成 `OpenWorkmate-pdf-b.pdf`，供 **PD05** `pdf_merge` 使用。可选：自备一份含可选中文字的小 PDF 做附件 + `get_pdf_text`（扫描件可能几乎无字，属预期）。  
6. 可选：**F04/F05** `text_file_write` / `text_file_read` 用 `OpenWorkmate-text-test.md`（与 §3.2 一致）。

### 3.1 Browser


| 编号  | 工具名                      | 前置           | 建议粘贴到对话框的话术                                                   | 应核对工具名                   | 预期要点           |
| --- | ------------------------ | ------------ | ------------------------------------------------------------- | ------------------------ | -------------- |
| B01 | `highlight_webpage_text` | 普通网页有一段可见正文  | 「请用 highlight_webpage_text 高亮词语【测试】，颜色 yellow。」               | `highlight_webpage_text` | 页面高亮           |
| B02 | `add_floating_note`      | 同上           | 「请 add_floating_note：标题【手工测试】，内容【Browser 便签】，anchorText【测试】。」 | `add_floating_note`      | 便签可拖动          |
| B03 | `run_builtin_page_script`        | 同上           | 「请 run_builtin_page_script：scriptId=get_page_title，paramsJson={}。」    | `run_builtin_page_script`        | 返回标题等          |
| B04 | `run_custom_javascript_in_page` | WebSocket 正常 | 「请 run_custom_javascript_in_page：`return document.title`（需确认则先说明）。」  | `run_custom_javascript_in_page` | 标题或确认流         |
| B05 | `capture_full_page`      | 同上           | 「请 capture_full_page，回复里写出 screenshot 引用。」                    | `capture_full_page`      | `screenshot:…` |
| B06 | `run_builtin_page_script`        | 同上           | 「请 run_builtin_page_script：scriptId=get_page_outline，paramsJson 含 includeTextPrefix true、maxLength 400。」 | `run_builtin_page_script`        | 返回 url、title、headings、可选 text_prefix |
| B07 | `run_builtin_page_script`        | 含表格的网页      | 「请 run_builtin_page_script：scriptId=extract_tables，paramsJson 含 maxTables 2、maxRows 10。」              | `run_builtin_page_script`        | Markdown 表格片段或「未找到」说明        |
| B08 | `run_builtin_page_script`        | 当前窗口至少两个标签  | 「请 run_builtin_page_script：scriptId=tab_list，paramsJson 含 maxTabs 20。」                          | `run_builtin_page_script`        | JSON 含 tabId、active 等           |
| B09 | `run_builtin_page_script`        | 同上           | 「请 run_builtin_page_script：scriptId=wait_for_selector，paramsJson 含 selector 为 body、timeoutMs 3000。」   | `run_builtin_page_script`        | 成功找到元素或超时原因明确             |
| B10 | `run_builtin_page_script`        | 任选正文很长的网页（或本地长 HTML） | 「请 run_builtin_page_script：scriptId=get_visible_text，paramsJson 含 maxLength 2000、truncateMode tail。」 | `run_builtin_page_script`        | 返回中含「末尾」或省略提示，且为页底附近文本而非仅页头 |
| B11 | `run_builtin_page_script`        | 任意常见 AI 对话网页（或普通长文页） | 「请 run_builtin_page_script：scriptId=chat_page_tail_glance，paramsJson 可含 maxTailChars 8000。」 | `run_builtin_page_script`        | 返回含「泛化」「来源：」说明 + 偏末尾正文；无内容时提示改用 get_visible_text+tail |
| B12 | `run_custom_javascript_in_page` | WebSocket 正常、已开 Allow User Scripts | 「请 run_custom_javascript_in_page：`1+1`（无 return，需确认则先说明）。」 | `run_custom_javascript_in_page` | 工具返回须说明「空」或「需 return」，**不得**仅为泛化「已成功执行」而无原因 |

**说明（白名单）**：设置「安全与确认 → Chrome → 页面脚本」默认勾选与后端 `DefaultAllowedScriptIds` 对齐；**`tab_open` 不在默认列表**（可打开任意 URL），需手动添加 scriptId `tab_open` 后再测新开标签。`tab_close` 须传非当前活动标签的 `tabId`。


### 3.2 File


| 编号  | 工具名                            | 前置        | 建议粘贴到对话框的话术                                           | 应核对工具名                         | 预期要点   |
| --- | ------------------------------ | --------- | ----------------------------------------------------- | ------------------------------ | ------ |
| F01 | `get_attachment_path`          | 先附件一张图    | 「请对我上一张附件调用 get_attachment_path。」                     | `get_attachment_path`          | 本机路径   |
| F02 | `get_file_size`                | 有测试文件     | 「请 get_file_size：OpenWorkmate-excel-test.xlsx。」             | `get_file_size`                | 字节数    |
| F03 | `save_screenshot_to_downloads` | 已有 B05 引用 | 「请 save_screenshot_to_downloads，文件名 OpenWorkmate-fullpage。」 | `save_screenshot_to_downloads` | 下载目录有图 |
| F04 | `text_file_write` | — | 「请 text_file_write：相对路径 OpenWorkmate-text-test.md，content【# 手工测试换行后一行正文】，append false（覆盖或新建）。」 | `text_file_write` | 下载目录生成 UTF-8 文本 |
| F05 | `text_file_read` | F04 | 「请 text_file_read：OpenWorkmate-text-test.md，maxChars 100000。」 | `text_file_read` | 内容与 F04 一致 |


### 3.2a Pdf（内置，插件名 `Pdf`）

**说明**：`DisabledBuiltInPlugins` 含 `**pdf`** 时本节全部跳过。读工具路径可为下载目录相对名（同 §3）；或先 **附件 PDF** 再 `get_attachment_path` 得到绝对路径。扫描件可能抽不到字，工具返回会提示可配合 OCR；加密无密码时应返回明确失败原因。


| 编号   | 工具名                                    | 前置             | 建议粘贴到对话框的话术                                                                                                                | 应核对工具名                | 预期要点                   |
| ---- | -------------------------------------- | -------------- | -------------------------------------------------------------------------------------------------------------------------- | --------------------- | ---------------------- |
| PD01 | `pdf_document_create`                  | —              | 「请必须调用工具 pdf_document_create：输出 OpenWorkmate-pdf-a.pdf，正文【第一段手工测PDF】。overwrite 为 false。」                                         | `pdf_document_create` | 下载目录生成可打开 PDF          |
| PD02 | `get_pdf_info`                         | PD01           | 「请 get_pdf_info：OpenWorkmate-pdf-a.pdf。」                                                                                         | `get_pdf_info`        | 页数、是否加密、元数据摘要          |
| PD03 | `get_pdf_text`                         | PD01           | 「请 get_pdf_text：OpenWorkmate-pdf-a.pdf，maxChars 50000。」（可不写 firstPage、lastPage）                                                  | `get_pdf_text`        | 含 `Page` 分段或正文；过长有截断说明 |
| PD04 | `pdf_document_create`                  | —              | 「请 pdf_document_create：输出 OpenWorkmate-pdf-b.pdf，正文【第二份合并用】。overwrite false。」                                                    | `pdf_document_create` | 第二文件存在                 |
| PD05 | `pdf_merge`                            | PD01 且 PD04    | 「请 pdf_merge：输出 OpenWorkmate-pdf-merged.pdf；inputPdfPaths 里分两行写 OpenWorkmate-pdf-a.pdf 与 OpenWorkmate-pdf-b.pdf（或同一行用分号分隔）；overwrite false。」 | `pdf_merge`           | 合并成功，总页数合理             |
| PD06 | `get_attachment_path` + `get_pdf_text` | 侧栏附件一份非扫描小 PDF | 「请先对我附件调用 get_attachment_path，再对返回路径调用 get_pdf_text，maxChars 200000。」                                                      | 两个工具名                 | 两跳均成功；无字时返回说明非静默       |


**负向（可选）**：`pdf_document_create` 目标已存在且 `overwrite=false` 时应失败并含原因；`pdf_merge` 仅给一个路径时应失败并含原因。

### 3.3 System


| 编号  | 工具名                | 建议粘贴到对话框的话术                           | 应核对工具名             | 预期要点   |
| --- | ------------------ | ------------------------------------- | ------------------ | ------ |
| S01 | `get_current_time` | 「请调用 get_current_time，给出本地与 UTC，不要猜。」 | `get_current_time` | 与系统钟一致 |


### 3.4 UserOptions


| 编号  | 工具名           | 前置   | 建议粘贴到对话框的话术                                   | 应核对工具名        | 预期要点       |
| --- | ------------- | ---- | --------------------------------------------- | ------------- | ---------- |
| U01 | `ask_options` | 侧栏打开 | 「请 ask_options：步骤1 问格式 JSON/CSV；步骤2 问表头 是/否。」 | `ask_options` | 分步 UI + 汇总 |


### 3.5 MCP_STT


| 编号  | 工具名                | 前置            | 建议粘贴到对话框的话术                           | 应核对工具名             | 预期要点    |
| --- | ------------------ | ------------- | ------------------------------------- | ------------------ | ------- |
| T01 | `transcribe_audio` | 下载目录有 mp3/wav | 「请 transcribe_audio：文件【你的文件名】，在下载目录。」 | `transcribe_audio` | 文本或配置错误 |


### 3.6 MCP_OCR


| 编号  | 工具名         | 前置               | 建议粘贴到对话框的话术                   | 应核对工具名      | 预期要点 |
| --- | ----------- | ---------------- | ----------------------------- | ----------- | ---- |
| O01 | `ocr_image` | 有 OpenWorkmate-img.png | 「请 ocr_image：OpenWorkmate-img.png。」 | `ocr_image` | 识别文字 |


### 3.7 CLI


| 编号  | 工具名           | 前置     | 建议粘贴到对话框的话术                                    | 应核对工具名        | 预期要点   |
| --- | ------------- | ------ | ---------------------------------------------- | ------------- | ------ |
| L01 | `run_command` | 知白名单策略 | 「请 run_command：`cmd /c echo OpenWorkmate-cli-test`。」 | `run_command` | 输出或确认流 |


### 3.8 Excel（逐工具）

**文件**：`OpenWorkmate-excel-test.xlsx`（相对路径 = 用户「下载」目录）。**建议按 E01→E21 顺序**。


| 编号  | 工具名                              | 依赖  | 建议粘贴到对话框的话术                                                                                                                                             | 应核对工具名                           | 预期要点             |
| --- | -------------------------------- | --- | ------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------- | ---------------- |
| E01 | `excel_range_write`              | —   | 「请 excel_range_write：文件 OpenWorkmate-excel-test.xlsx，Sheet1，A1，data=[[姓名,分数,等级],[张三,85,],[李四,92,]]。」                                                      | `excel_range_write`              | 已写入              |
| E02 | `excel_sheets_list`              | E01 | 「请 excel_sheets_list：OpenWorkmate-excel-test.xlsx。」                                                                                                           | `excel_sheets_list`              | 列出表名             |
| E03 | `excel_range_read`               | E01 | 「请 excel_range_read：同一文件，Sheet1，startCell A1，endCell C3，includeFormulas false。」                                                                         | `excel_range_read`               | 制表符文本            |
| E04 | `excel_formula_write`            | E01 | 「请在 Sheet1 的 D2 写入公式 =B2*1.1。」                                                                                                                          | `excel_formula_write`            | 成功               |
| E05 | `excel_cells_merge`              | E01 | 「请合并 Sheet1 的 A1:C1。」                                                                                                                                   | `excel_cells_merge`              | 已合并              |
| E06 | `excel_cells_unmerge`            | E05 | 「请取消合并 A1:C1。」                                                                                                                                          | `excel_cells_unmerge`            | 已取消              |
| E07 | `excel_named_range_define`       | E01 | 「请定义命名区域 ManualTestData，引用 Sheet1!A2:C3。」                                                                                                               | `excel_named_range_define`       | 成功               |
| E08 | `excel_named_range_read`         | E07 | 「请 excel_named_range_read：name=ManualTestData。」                                                                                                         | `excel_named_range_read`         | 数据一致             |
| E09 | `excel_named_ranges_list`        | E07 | 「请 excel_named_ranges_list。」                                                                                                                            | `excel_named_ranges_list`        | 含 ManualTestData |
| E10 | `excel_column_width_set`         | E01 | 「请 excel_column_width_set：Sheet1，columnIndex=2，width=20。」                                                                                               | `excel_column_width_set`         | 成功               |
| E11 | `excel_row_height_set`           | E01 | 「请 excel_row_height_set：Sheet1，rowIndex=1，height=22。」                                                                                                   | `excel_row_height_set`           | 成功               |
| E12 | `excel_validation_set`           | E01 | 「请对 Sheet1 区域 E2:E10 设 list 验证，formula1=优,良,差。」                                                                                                         | `excel_validation_set`           | 成功               |
| E13 | `excel_validations_list`         | E12 | 「请 excel_validations_list：Sheet1。」                                                                                                                      | `excel_validations_list`         | 有规则              |
| E14 | `excel_validation_clear`         | E12 | 「请清除 Sheet1 的 E2:E10 验证。」                                                                                                                               | `excel_validation_clear`         | 已清除              |
| E15 | `excel_conditional_format_add`   | E01 | 「请对 Sheet1 的 B2:B10 添加 between 条件格式，formula1=60，formula2=100。」                                                                                          | `excel_conditional_format_add`   | 成功               |
| E16 | `excel_conditional_formats_list` | E15 | 「请 excel_conditional_formats_list：Sheet1。」                                                                                                              | `excel_conditional_formats_list` | 有规则              |
| E17 | `excel_conditional_format_clear` | E15 | 「请清除 Sheet1 的 B2:B10 条件格式。」                                                                                                                             | `excel_conditional_format_clear` | 已清除              |
| E18 | `excel_hyperlink_set`            | E01 | 「请 excel_hyperlink_set：文件 OpenWorkmate-excel-test.xlsx，Sheet1，单元格 F1，url 填 https://example.com ，displayText 填【测试链接】。」 | `excel_hyperlink_set`            | 成功               |
| E19 | `excel_sheet_add`                | E01 | 「请添加工作表 ManualTestExtra。」                                                                                                                               | `excel_sheet_add`                | 新表存在             |
| E20 | `excel_sheet_remove`             | E19 | 「请删除工作表 ManualTestExtra。」                                                                                                                               | `excel_sheet_remove`             | 已删               |
| E21 | `excel_charts_list`              | —   | 「请 excel_charts_list：OpenWorkmate-excel-test.xlsx。」（需非空可先手工插入图表）                                                                                              | `excel_charts_list`              | 列表或「无图表」         |


### 3.9 Word（逐工具，共 23 个函数）

**文件**：`OpenWorkmate-word-test.docx`。**无表格时** `word_tables_list` / `word_tables_read` 会得到「无表格」——可先在 Word 手工插入 2×2 表再测 W02/W03，或接受「无表格」作为预期。

**说明**：`word_document_create` 有 **W01**（空行拆段）与 **W01b**（换行/空行场景）两条；其余每行对应一个内置工具函数。竖线分段与表格语法冲突处见第 3 节段首「表格与 Markdown」。


| 编号   | 工具名                         | 依赖               | 建议粘贴到对话框的话术                                                                                                                             | 应核对工具名                      | 预期要点        |
| ---- | --------------------------- | ---------------- | --------------------------------------------------------------------------------------------------------------------------------------- | --------------------------- | ----------- |
| W01  | `word_document_create`      | —                | 「请 word_document_create：OpenWorkmate-word-test.docx，标题【手工测试】；**paragraphs 用字符串数组**，两个元素分别为【第一段】与【第二段】。」（与工具说明一致时也可只传一项，项内用空行或 ASCII 竖线 U+007C 分段；本表为避表格语法不示例竖线。） | `word_document_create`      | 文件已生成，含两段正文 |
| W01b | `word_document_create`      | —                | 「请 word_document_create：OpenWorkmate-word-newlines.docx，标题【换行分段】；**paragraphs 用字符串数组**：第一项为多行字符串——第一行【段甲】、空一行、再【段乙】；不要用数组项之间的竖线代替空行分段。」                                 | `word_document_create`      | 多段按空行/换行拆段  |
| W02  | `word_body_read`            | W01              | 「请 word_body_read：OpenWorkmate-word-test.docx，includeTables true。」                                                                            | `word_body_read`            | 段落文本        |
| W03  | `word_tables_list`          | 文档内有表            | 「请 word_tables_list：OpenWorkmate-word-test.docx。」                                                                                             | `word_tables_list`          | 表数量或「无表格」   |
| W04  | `word_tables_read`          | 有表               | 「请 word_tables_read：tableIndex=1。」                                                                                                      | `word_tables_read`          | 表内容         |
| W05  | `word_find_replace`         | W01              | 「请 word_find_replace：查找文档中已有词【第一段】，替换为【已替换】。」（若坚持用【替换目标】，需先在正文加入该词再替换）                                                                  | `word_find_replace`         | 成功          |
| W06  | `word_paragraphs_format`    | W01              | 「请 word_paragraphs_format：第 2 段，alignment center。」                                                                                      | `word_paragraphs_format`    | 成功          |
| W07  | `word_text_format`          | W05              | 「请 word_text_format：包含文字【已替换】，加粗、红色。」                                                                                                   | `word_text_format`          | 成功          |
| W08  | `word_comments_list`        | 可有批注             | 「请 word_comments_list。」                                                                                                                 | `word_comments_list`        | 列表或空        |
| W09  | `word_comment_add`          | W05              | 「请 word_comment_add：在含【已替换】处加批注【测试批注】。」                                                                                                 | `word_comment_add`          | 成功          |
| W10  | `word_comments_read`        | W09              | 「请 word_comments_read。」                                                                                                                 | `word_comments_read`        | 批注内容        |
| W11  | `word_comments_delete`      | W09              | 「请 word_comments_delete：删除刚才的批注（或指定 commentId）。」                                                                                        | `word_comments_delete`      | 成功          |
| W12  | `word_headers_footers_list` | W01              | 「请 word_headers_footers_list。」                                                                                                          | `word_headers_footers_list` | 条数与各索引文本摘要  |
| W13  | `word_header_read`          | 有页眉              | 「请 word_header_read：index=1。」                                                                                                           | `word_header_read`          | 文本          |
| W14  | `word_footer_read`          | 有页脚              | 「请 word_footer_read：index=1。」                                                                                                           | `word_footer_read`          | 文本          |
| W15  | `word_header_write`         | W01              | 「请 word_header_write：index=1，文本【页眉手工测试】。」                                                                                               | `word_header_write`         | 成功          |
| W16  | `word_footer_write`         | W01              | 「请 word_footer_write：index=1，文本【页脚手工测试】。」                                                                                               | `word_footer_write`         | 成功          |
| W17  | `word_bookmark_insert`      | W01              | 「请 word_bookmark_insert：书签名 bm_manual，paragraphIndex=1。」                                                                                | `word_bookmark_insert`      | 成功          |
| W18  | `word_bookmarks_list`       | W17              | 「请 word_bookmarks_list。」                                                                                                                | `word_bookmarks_list`       | 含 bm_manual |
| W19  | `word_bookmark_read`        | W17              | 「请 word_bookmark_read：bm_manual。」                                                                                                       | `word_bookmark_read`        | 文本          |
| W20  | `word_image_insert`         | 有 OpenWorkmate-img.png | 「请 word_image_insert：第 1 段后插入 OpenWorkmate-img.png。」                                                                                          | `word_image_insert`         | 成功          |
| W21  | `word_images_list`          | W20 后            | 「请 word_images_list。」                                                                                                                   | `word_images_list`          | 部件数 ≥1      |
| W22  | `word_sections_list`        | W01              | 「请 word_sections_list。」                                                                                                                 | `word_sections_list`        | ≥1 节        |
| W23  | `word_hyperlink_insert`     | W01              | 「请 word_hyperlink_insert：在第 2 段插入超链接，地址 https://example.com ，显示文字【点我】。」                                | `word_hyperlink_insert`     | 成功          |


### 3.10 Ppt（逐工具，共 14 个函数）

**文件**：`OpenWorkmate-ppt-test.pptx`。**P14** 可复制**含嵌入图**的页（`ImagePart` + `blip/@embed`）；亦可在 **P01** 无图页上测；复杂图表/媒体若失败记工具返回。

**说明**：`ppt_table_write_cells` 的 `rowsCsv` 约定见插件 Description（多行用 U+007C、单元格用英文逗号）；**P12** 话术用「竖线」代称，避免破坏表格（参见 §3「表格与 Markdown」）。

**读工具（可选回归）**：在多轮对话后（助手已口头描述过幻灯片内容），再单独发一条与 **P03** 相同话术「请 ppt_slide_read：…」。服务端**不会**自动续跑第二轮；应观察模型是否在本轮直接调用 `ppt_slide_read`，否则可再发一条明确要求或先 `activate_tools` 等。


| 编号  | 工具名                     | 依赖               | 建议粘贴到对话框的话术                                                                                                                            | 应核对工具名                  | 预期要点    |
| --- | ----------------------- | ---------------- | -------------------------------------------------------------------------------------------------------------------------------------- | ----------------------- | ------- |
| P01 | `ppt_document_create`   | —                | 「请 ppt_document_create：OpenWorkmate-ppt-test.pptx。」                                                                                          | `ppt_document_create`   | 1 张灯片   |
| P02 | `ppt_slides_list`       | P01              | 「请 ppt_slides_list。」                                                                                                                   | `ppt_slides_list`       | ≥1 张    |
| P03 | `ppt_slide_read`        | P01              | 「请 ppt_slide_read：slideIndex=1，includeShapeDetails true。」                                                                              | `ppt_slide_read`        | 文本+形状   |
| P04 | `ppt_slide_write`       | P03              | 「请 ppt_slide_write：slideIndex=1，placeholderType=title，text【手工 PPT】。」                                                                   | `ppt_slide_write`       | 成功      |
| P05 | `ppt_slide_insert`      | P01              | 「请 ppt_slide_insert：position 取大末尾，title【第二页】，body【正文测试】。」                                                                              | `ppt_slide_insert`      | 总页数 +1  |
| P06 | `ppt_slide_delete`      | P05              | 「请 ppt_slide_delete：slideIndex=2。」                                                                                                     | `ppt_slide_delete`      | 剩 1 页   |
| P07 | `ppt_slide_image_add`   | 有 OpenWorkmate-img.png | 「请 ppt_slide_image_add：slideIndex=1，imagePath OpenWorkmate-img.png。」                                                                         | `ppt_slide_image_add`   | 成功      |
| P08 | `ppt_notes_read`        | P01              | 「请 ppt_notes_read：slideIndex=1。」                                                                                                       | `ppt_notes_read`        | 文本或空    |
| P09 | `ppt_notes_write`       | P01              | 「请 ppt_notes_write：slideIndex=1，【备注手工测试】。」                                                                                             | `ppt_notes_write`       | 成功      |
| P10 | `ppt_slides_reorder`    | 至少 2 页           | 先 P05 再发：「请 ppt_slides_reorder：newOrder=2,1。」                                                                                          | `ppt_slides_reorder`    | 成功      |
| P11 | `ppt_table_create`      | P01              | 「请 ppt_table_create：slideIndex=1，3 行 2 列。」                                                                                             | `ppt_table_create`      | 成功      |
| P12 | `ppt_table_write_cells` | P11              | 「请 ppt_table_write_cells：slideIndex=1，rowsCsv 填 2 行 2 列：第一行【第一行左,第一行右】与第二行【第二行左,第二行右】之间用竖线（U+007C）连接，格内只用英文逗号。」                        | `ppt_table_write_cells` | 成功      |
| P13 | `ppt_hyperlink_add`     | P01              | 「请 ppt_hyperlink_add：文件 OpenWorkmate-ppt-test.pptx，slideIndex=1，url https://example.com ，shapeIndex=1。」 | `ppt_hyperlink_add`     | 成功或形状说明 |
| P14 | `ppt_slide_duplicate`   | P01 或 P07        | 「请 ppt_slide_duplicate：slideIndex=1。」后端复制 `ImagePart` 并重映射 `blip/@embed`；极复杂页（图表等）失败请看工具返回。                                            | `ppt_slide_duplicate`   | 成功      |


### 3.11 联网搜索（百炼 enable_search）


| 编号  | 工具名 | 前置                            | 建议粘贴到对话框的话术                         | 应核对工具名 | 预期要点                               |
| --- | --- | ----------------------------- | ----------------------------------- | ------ | ---------------------------------- |
| WS1 | （无） | 当前模型为百炼兼容地址且已开启 enable_search | 「请根据公开网络信息简要说明【2026 年某项科技新闻关键词】近况。」 | —      | 回答含检索依据或来源说明；**无** `tavily_*` 工具调用 |


### 3.12 ClawhubSkill


| 编号  | 工具名                  | 前置    | 建议粘贴到对话框的话术                                                  | 应核对工具名               | 预期要点 |
| --- | -------------------- | ----- | ------------------------------------------------------------ | -------------------- | ---- |
| CH1 | `run_clawhub_script` | 已配置技能 | 「请 run_clawhub_script：scriptName=【你的脚本名】，arguments=【空或按技能】。」 | `run_clawhub_script` | 脚本输出 |


### 3.13 Memory


| 编号  | 工具名             | 前置        | 建议粘贴到对话框的话术         | 应核对工具名          | 预期要点  |
| --- | --------------- | --------- | ------------------- | --------------- | ----- |
| ME1 | `save_memory`   | Embedding | 「请记住：手工测试记忆前缀是 ME。」 | `save_memory`   | 成功    |
| ME2 | `search_memory` | ME1       | 「我之前让你记住的前缀是什么？」    | `search_memory` | 命中 ME |


### 3.14 AccurateData


| 编号  | 工具名                    | 建议粘贴到对话框的话术                                             | 应核对工具名                 | 预期要点 |
| --- | ---------------------- | ------------------------------------------------------- | ---------------------- | ---- |
| AD1 | `accurate_data_write`  | 「accurate_data_write：id=manual-ad-1，format=md，内容 # 测试。」 | `accurate_data_write`  | 成功   |
| AD2 | `accurate_data_read`   | 「accurate_data_read：manual-ad-1。」                       | `accurate_data_read`   | 一致   |
| AD3 | `accurate_data_list`   | 「accurate_data_list：前缀 manual-ad。」                      | `accurate_data_list`   | 含 id |
| AD4 | `accurate_data_delete` | 「accurate_data_delete：manual-ad-1。」                     | `accurate_data_delete` | 已删   |


### 3.15 MeetingTranscript


| 编号  | 工具名                       | 前置          | 建议粘贴到对话框的话术                                                              | 应核对工具名                    | 预期要点       |
| --- | ------------------------- | ----------- | ------------------------------------------------------------------------ | ------------------------- | ---------- |
| MT1 | `meeting_transcript_meta` | 有 sessionId | 「meeting_transcript_meta：sessionId=【meeting_xxx】。」                       | `meeting_transcript_meta` | totalChars |
| MT2 | `meeting_transcript_read` | 同上          | 「meeting_transcript_read：offsetChars=0，maxChars=16000，直到 hasMore=false。」 | `meeting_transcript_read` | 分块文本       |


### 3.16 ScheduledTask

**说明**：`scheduled_task_create` **必填** `title` 与 **Markdown 正文** `content`（任务到点时要执行的说明）。`scheduleType` 默认为 `**cron`**：若只写 `intervalMinutes` 而不写 `cronExpression`，`nextRunAt` 可能为空。测「每 N 分钟重复」时请显式 `**scheduleType=interval**` 并配合 `intervalMinutes` 或 `intervalSeconds`。会话 id 以 `**scheduled:**` 开头时，创建/更新/删除类工具会被屏蔽（防套娃），见 `ClientTypeToolFilter`。


| 编号  | 工具名                     | 建议粘贴到对话框的话术                                                                                                     | 应核对工具名                  | 预期要点                          |
| --- | ----------------------- | --------------------------------------------------------------------------------------------------------------- | ----------------------- | ----------------------------- |
| ST1 | `scheduled_task_create` | 「请 scheduled_task_create：title【手工测试】，content【到点后请回复一句：定时任务手工回归。】，scheduleType【interval】，intervalMinutes【1440】。」 | `scheduled_task_create` | 返回 id 且 **nextRunAt** 为合理未来时间 |
| ST2 | `scheduled_task_list`   | 「scheduled_task_list：enabledOnly true。」                                                                         | `scheduled_task_list`   | 含 ST1                         |
| ST3 | `scheduled_task_read`   | 「scheduled_task_read：id=【上一步】。」                                                                                 | `scheduled_task_read`   | 内容与 meta 可读                   |
| ST4 | `scheduled_task_update` | 「scheduled_task_update：id=【同上】，enabled=false。」                                                                  | `scheduled_task_update` | 成功                            |
| ST5 | `scheduled_task_delete` | 「scheduled_task_delete：id=【同上】。」                                                                                | `scheduled_task_delete` | 已删                            |


**可选 ST6（cron）**：`scheduleType=cron`，`cronExpression` 使用标准 5 字段（如每天固定时刻），可配 `timeZone`（如 `China Standard Time`）；若表达式非法，工具应返回明确错误而非静默成功。

### 3.17 Context


| 编号  | 工具名                    | 建议粘贴到对话框的话术                          | 应核对工具名                 | 预期要点      |
| --- | ---------------------- | ------------------------------------ | ---------------------- | --------- |
| CX1 | `compact_conversation` | 多轮闲聊后：「请 compact_conversation 压缩上文。」 | `compact_conversation` | 摘要 + 可继续聊 |


### 3.18 Subagent


| 编号  | 工具名           | 建议粘贴到对话框的话术                          | 应核对工具名        | 预期要点    |
| --- | ------------- | ------------------------------------ | ------------- | ------- |
| SB1 | `run_subtask` | 「run_subtask：任务描述『用 3 条要点说明单元测试概念』。」 | `run_subtask` | 主对话仅短总结 |


### 3.19 CrossAgentTask


| 编号  | 工具名                         | 建议粘贴到对话框的话术                                                                                                                                  | 应核对工具名                      | 预期要点  |
| --- | --------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------- | ----- |
| CA1 | `create_cross_agent_task`   | 「create_cross_agent_task：targetClientType=office-word，描述【下次打开任务窗格提示手工测试】。」（合法取值还有 **chrome**、**office-excel**、**office-powerpoint**、**wps**） | `create_cross_agent_task`   | 任务已创建 |
| CA2 | `complete_cross_agent_task` | 「complete_cross_agent_task：任务 id=…，状态 done。」（需有效 id）                                                                                         | `complete_cross_agent_task` | 成功    |


### 3.20 Plan


| 编号  | 工具名                 | 前置   | 建议粘贴到对话框的话术                      | 应核对工具名              | 预期要点   |
| --- | ------------------- | ---- | -------------------------------- | ------------------- | ------ |
| PL1 | `create_plan`       | 无    | 「create_plan：目标【整理下载目录测试文件清单】。」  | `create_plan`       | planId |
| PL2 | `get_plan`          | PL1  | 「get_plan：planId=…。」             | `get_plan`          | 全文     |
| PL3 | `update_plan`       | PL1  | 「update_plan：追加一行【手工备注】。」        | `update_plan`       | 成功     |
| PL4 | `execute_plan_step` | PL1  | 「execute_plan_step：stepIndex=1。」 | `execute_plan_step` | 执行步骤   |
| PL5 | `complete_plan`     | 步骤完成 | 「complete_plan。」                 | `complete_plan`     | 完成     |


### 3.21 SkillAuthor


| 编号  | 工具名                        | 建议粘贴到对话框的话术                                           | 应核对工具名                     | 预期要点   |
| --- | -------------------------- | ----------------------------------------------------- | -------------------------- | ------ |
| SK1 | `generate_user_skill`      | 「generate_user_skill：goal=【Chrome 手工回归清单】，context 空。」 | `generate_user_skill`      | 设置页新技能 |
| SK2 | `save_user_skill_markdown` | 模型已输出 SKILL.md 时：「save_user_skill_markdown：用上文全文。」    | `save_user_skill_markdown` | 成功     |


### 3.22 AgentTooling 与 UserSkillProgressive（动态工具）

> **前提**：主会话固定为动态工具（首轮 bootstrap）。**US*** 依赖设置中**至少启用一个用户技能**。


| 编号  | 工具名                         | 前置                    | 建议粘贴到对话框的话术                                                                 | 应核对工具名                        | 预期要点                    |
| --- | --------------------------- | --------------------- | --------------------------------------------------------------------------- | ----------------------------- | ----------------------- |
| DT1 | `search_available_tools`      | 动态工具开启                | 「请 search_available_tools：query【excel 写入】。」                                  | `search_available_tools`      | 返回若干裸函数名（如 `excel_range_write`） |
| DT2 | `activate_tools`            | DT1 之后、已看到目标函数名       | 「请 activate_tools：只激活【excel_range_read】。」（按你环境把数组换成上一步真实函数名）                 | `activate_tools`              | 工具返回成功；后续轮次可实际调用已激活工具   |
| US1 | `search_available_skills`   | 已启用用户技能               | 「请 search_available_skills：query【手工】。」                                       | `search_available_skills`     | 返回技能 id 列表              |
| US2 | `select_skill_for_turn`     | US1 之后                | 「请 select_skill_for_turn：skillId【上一步某一 id】。」                              | `select_skill_for_turn`       | 成功                      |
| US3 | `load_user_skill_instructions` | US2 之后（或已知 Id 时可直接 load）     | 「请 load_user_skill_instructions：skillId【与 US2 相同】。」                        | `load_user_skill_instructions` | 返回 SKILL 正文片段（或配置错误说明）   |


**与 `activate_tools` 的门禁**：若本轮已调用过 `search_available_tools` 且存在已启用用户技能，须先至少调用一次 `search_available_skills`，再 `activate_tools`（见 `应用内AI插件列表.md` §1 工具链说明）。


---

## 四、选项页（options）与配置


| 序号  | 场景          | 操作           | 预期                                                                                                                                                    |
| --- | ----------- | ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| O1  | 内置插件列表      | 打开内置工具/插件区块  | **运行时**以 ToolRegistry 为准；`/api/tools/builtin` **当前返回 18 条、仍缺 5 项**（见 `**docs/应用内AI插件列表.md` §五**）；以该文档 **§1.1** 核对 `disabledBuiltInPlugins`；停用某插件后对话中不可再调用该插件工具 |
| O2  | 保存配置        | 修改模型/密钥/目录   | 保存成功；侧栏重连后生效                                                                                                                                          |
| O3  | MCP 服务器（外部） | 若配置外部 MCP    | Chrome 会话可出现 `MCP`_ 前缀的外部工具；**本计划不强制逐项**（以运行时 tools/list 为准）                                                                                          |
| O4  | 用户技能        | 启用/禁用某 Skill | 侧栏 `@` 中 Skills 列表变化                                                                                                                                  |


---

## 五、调试与可观测（可选）


| 序号  | 场景          | 操作                                                                   | 预期                             |
| --- | ----------- | -------------------------------------------------------------------- | ------------------------------ |
| D1  | debug-stats | 从选项页打开调试统计页（若存在）                                                     | 能看到会话、向量检索等统计，无白屏              |
| D2  | 日志          | 后端控制台/日志文件                                                           | 出错时有异常栈或说明                     |
| D3  | 时间线 ↔ WS    | 多工具一轮对话时对照后端日志中的 `reasoning_chunk`、`agent_phase`、`tool_invocation_*` | 侧栏 §1.6 时间线块顺序与发帧顺序一致；无顶栏整轮读秒条 |


---

## 六、内置插件与工具覆盖核对表（Chrome）

在以下插件**未**被 `disabledBuiltInPlugins`（**小写** id，如 `pdf`、`scheduledtask`）停用、且依赖配置已满足时，**Chrome 端应可测到**下列工具（`CurrentDocument` 整组跳过）。**Pdf** 插件 id 为 `**pdf**`。各插件 `**[ToolFunction]**` 个数与 `**docs/应用内AI插件列表.md` §1.3** 一致；`/api/tools/builtin` **未必列出** Pdf 等 5 项（§五）。


| 插件                | 工具函数名（`ToolFunction`）                                                                                                   |
| ----------------- | ----------------------------------------------------------------------------------------------------------------------- |
| Browser           | `highlight_webpage_text`, `add_floating_note`, `run_builtin_page_script`, `run_custom_javascript_in_page`, `capture_full_page`         |
| File              | `get_attachment_path`, `get_file_size`, `save_screenshot_to_downloads`, `text_file_read`, `text_file_write`               |
| Pdf               | `get_pdf_text`, `get_pdf_info`, `pdf_document_create`, `pdf_merge`                                                      |
| System            | `get_current_time`                                                                                                      |
| UserOptions       | `ask_options`                                                                                                           |
| MCP_STT           | `transcribe_audio`                                                                                                      |
| MCP_OCR           | `ocr_image`                                                                                                             |
| CLI               | `run_command`                                                                                                           |
| Excel             | 见 §3.8 共 21 个                                                                                                           |
| Word              | 见 §3.9 共 23 个                                                                                                           |
| Ppt               | 见 §3.10 共 14 个                                                                                                          |
| ClawhubSkill      | `run_clawhub_script`                                                                                                    |
| Memory            | `save_memory`, `search_memory`                                                                                          |
| AccurateData      | `accurate_data_write`, `accurate_data_read`, `accurate_data_list`, `accurate_data_delete`                               |
| MeetingTranscript | `meeting_transcript_read`, `meeting_transcript_meta`                                                                    |
| ScheduledTask     | `scheduled_task_create`, `scheduled_task_list`, `scheduled_task_read`, `scheduled_task_update`, `scheduled_task_delete` |
| Context           | `compact_conversation`                                                                                                  |
| Subagent          | `run_subtask`                                                                                                           |
| CrossAgentTask    | `create_cross_agent_task`, `complete_cross_agent_task`                                                                  |
| Plan              | `create_plan`, `get_plan`, `update_plan`, `execute_plan_step`, `complete_plan`                                          |
| SkillAuthor       | `generate_user_skill`, `save_user_skill_markdown`                                                                       |
| AgentTooling      | `search_available_tools`, `activate_tools`（动态工具开启时；见 §3.22）                                                          |
| UserSkillProgressive | `search_available_skills`, `select_skill_for_turn`, `load_user_skill_instructions`（见 §3.22）                          |


**不计入「内置插件」但会出现的**：`MCP_`*（外部 MCP）。用户技能已改为 **UserSkillProgressive** 三工具 + system 元数据（见 §3.22、§1.4、§四 O4）；外部 MCP 按各自配置单测。

---

## 七、记录模板（测试时可复制）

**工具调用成功率**：`命中次数 / 计划用例数`，其中「命中」指日志或 UI 中可见对应工具函数被调用（模型未调用记为 MISS）。

```
日期：
Chrome / 扩展版本：
后端版本：

| 用例编号 | 目标工具名 | 工具调用命中 Y/N | 结果 PASS/FAIL | 现象与备注 |
|----------|--------------|-------------------|----------------|------------|
| E02 | excel_sheets_list | | | |
| … | | | | |
```

---

*文档依据仓库内 `ClientTypeToolFilter`、`ChatService.RebuildRuntimeAsync`、`Program.cs`（`GET /api/tools/builtin`）、`docs/应用内AI插件列表.md`（§1.1 / §1.3 / §五）与各 `*Plugin.cs` 整理；**校准日期：2026-04-15**（WPS：`wpsHostKind` 收紧 `CurrentDocument` 与动态工具索引；builtin 条数仍以 §六 表为准）。若后端增删工具，以代码为准同步 `docs/应用内AI插件列表.md` 与本文 §六。Chrome 侧栏「哪些 WS 进时间线」以 `chrome-extension/sidepanel.js` 中 `handleMessage` 上方注释（`reasoning_chunk` / `stream_warning` / `tool_invocation_*` 等）为准。*