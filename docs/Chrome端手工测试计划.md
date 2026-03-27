# Chrome 端功能手工测试计划

> **范围**：仅针对 **Chrome 扩展**（`chrome-extension/`）+ 本机 **Office Copilot Server**；测试素材与操作在 Chrome 内完成。  
> **不包含**：Cursor/VSCode 侧自行配置的 MCP、Office/WPS 任务窗格专属能力。  
> **内置工具定义**：与后端 Kernel 注册一致；**Chrome 的 `clientType` 为 `chrome` 时，不暴露 `CurrentDocument` 插件**（其余内置插件在满足配置前提下均可暴露）。详见 `backend/Services/ClientTypeToolFilter.cs`。

**通过标准（每条用例）**：行为符合描述；失败时 **HTTP 4xx/5xx 或工具返回中带明确原因**（见项目错误可见性约定），侧栏/选项页能展示服务端 `message`。

---

## 零、测试前环境检查

| 序号 | 检查项 | 说明 |
|------|--------|------|
| Z1 | 后端已启动 | 扩展能连上 API（侧栏状态或选项页「连接」正常） |
| Z2 | `chrome-extension` 已加载 | `chrome://extensions` 中已启用本扩展，版本与仓库一致 |
| Z3 | 访问密钥 / Token | 与 `user-config` 或选项页配置一致，请求不被 401 |
| Z4 | 模型与 Embedding 等 | 按需：Memory 需 Embedding；Tavily 需 `TAVILY_API_KEY`；OCR/STT 需选项页中对应配置 |
| Z5 | 下载目录 | File 保存截图、Word/Excel/Ppt 写本地文件时，路径解析以本机「下载」或配置为准 |

---

## 一、Chrome 扩展壳与通用交互

### 1.1 侧栏与页面

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| C1 | 打开侧栏 | 点击扩展图标或使用 Side Panel | 出现 `sidepanel.html` 对话界面 |
| C2 | 当前页标签 | 打开任意网页，查看侧栏「当前页」类提示（若有） | 与当前激活标签一致或合理提示 |
| C3 | 设置入口 | 点击设置/选项 | 打开 `options.html` 或等价入口 |
| C4 | 新会话 | 点击新对话 | 上下文清空，无串会话 |
| C5 | 停止生成 | 长回复中途点「停止」 | 流式停止，无长时间卡死 |
| C6 | 附件 | 点击附件，选图片/文件 | 预览区出现；发送后用户消息含 `attachment:` 引用 |

### 1.2 语音输入（扩展内，非 `transcribe_audio` 工具）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| V1 | 正常收音 | 点麦克风，说话后停止 | 文本进入输入框或作为用户消息（依赖实现） |
| V2 | 拒绝麦克风 | 浏览器拒绝麦克风权限 | 侧栏出现可理解的错误与「打开权限设置」类引导（若有） |
| V3 | 未配置百炼 ASR | 清空/错误 Key | 明确错误提示，非静默失败 |

### 1.3 会议监听（Chrome 侧栏）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| M0 | 开始监听 | 点会议监听，允许麦克风 | 生成 `meeting_…` 会话 ID，系统消息中可复制 sessionId |
| M1 | 实时转写 | 说话数句后查看实录区/独立页 | 有增量转写内容 |
| M2 | 结束 | 结束监听 | 落盘完成；可配合 **MeetingTranscript** 用例 |

### 1.4 `@` 模式与内置插件列表

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| A1 | 加载 Tools | 在输入框触发 `@`（或项目约定的唤起方式） | 列出 Tools + Skills；内置项与 `/api/tools/builtin` 一致（或说明部分需配置才注册） |
| A2 | 指定工具 | 选择某一内置插件名发送 | 对话中带工具约束，模型优先使用该方向能力 |

### 1.5 计划模式 UI（若侧栏已实现）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| P0 | 与 Plan 插件联动 | 开启计划模式后让 AI 建计划 | 出现 planId、步骤清单或等价 UI；与 `create_plan` / `execute_plan_step` 一致 |

---

## 二、不应在 Chrome 暴露的插件（负向）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| N1 | CurrentDocument | 用户明确要求「读取当前 Word 文档选区」（不要在 Office 里测） | 模型**不应**假装成功；若误调工具应返回明确不可用说明（Chrome 无任务窗格 RPC） |
| N2 | 工具列表 | 在调试统计或开发者手段中查看当前会话可用工具（若可查看） | 无 `CurrentDocument` 下工具 |

---

## 三、按内置插件与工具（逐项可测）

下列每条**工具名**与后端 `[KernelFunction("...")]` 一致。

- **路径约定**：未写盘符的**相对文件名**解析到当前 Windows 用户 **`Downloads`**（与后端 `OpenXmlHelpers.ResolvePath` 一致）。下文固定使用 `taskly-excel-test.xlsx`、`taskly-word-test.docx`、`taskly-ppt-test.pptx`、`taskly-img.png`，你可改名但同一轮请保持一致。
- **应核对工具名**：侧栏/调试统计/后端日志中是否出现该调用（用于统计**工具调用成功率**）。
- 模型未选型时：先发「**请必须调用 Kernel 工具 xxx**」，或用 `@Excel` 等约束。

### 3.0 数据准备顺序（建议）

1. **E01** `excel_range_write` 生成或覆盖 `taskly-excel-test.xlsx`。  
2. **W01** `word_document_create` 生成 `taskly-word-test.docx`。  
3. **P01** `ppt_document_create` 生成 `taskly-ppt-test.pptx`。  
4. 下载目录放一张 **`taskly-img.png`**，供 Word/Ppt 插图用例。

### 3.1 Browser

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| B01 | `highlight_webpage_text` | 普通网页有一段可见正文 | 「请用 highlight_webpage_text 高亮词语【测试】，颜色 yellow。」 | `highlight_webpage_text` | 页面高亮 |
| B02 | `add_floating_note` | 同上 | 「请 add_floating_note：标题【手工测试】，内容【Browser 便签】，anchorText【测试】。」 | `add_floating_note` | 便签可拖动 |
| B03 | `run_page_script` | 同上 | 「请 run_page_script：scriptId=get_page_title，paramsJson={}。」 | `run_page_script` | 返回标题等 |
| B04 | `run_custom_page_script` | WebSocket 正常 | 「请 run_custom_page_script：`return document.title`（需确认则先说明）。」 | `run_custom_page_script` | 标题或确认流 |
| B05 | `capture_full_page` | 同上 | 「请 capture_full_page，回复里写出 screenshot 引用。」 | `capture_full_page` | `screenshot:…` |

### 3.2 File

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| F01 | `get_attachment_path` | 先附件一张图 | 「请对我上一张附件调用 get_attachment_path。」 | `get_attachment_path` | 本机路径 |
| F02 | `get_file_size` | 有测试文件 | 「请 get_file_size：taskly-excel-test.xlsx。」 | `get_file_size` | 字节数 |
| F03 | `save_screenshot_to_downloads` | 已有 B05 引用 | 「请 save_screenshot_to_downloads，文件名 taskly-fullpage。」 | `save_screenshot_to_downloads` | 下载目录有图 |

### 3.3 System

| 编号 | 工具名 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------------------------|--------------|----------|
| S01 | `get_current_time` | 「请调用 get_current_time，给出本地与 UTC，不要猜。」 | `get_current_time` | 与系统钟一致 |

### 3.4 UserOptions

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| U01 | `ask_options` | 侧栏打开 | 「请 ask_options：步骤1 问格式 JSON/CSV；步骤2 问表头 是/否。」 | `ask_options` | 分步 UI + 汇总 |

### 3.5 MCP_STT

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| T01 | `transcribe_audio` | 下载目录有 mp3/wav | 「请 transcribe_audio：文件【你的文件名】，在下载目录。」 | `transcribe_audio` | 文本或配置错误 |

### 3.6 MCP_OCR

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| O01 | `ocr_image` | 有 taskly-img.png | 「请 ocr_image：taskly-img.png。」 | `ocr_image` | 识别文字 |

### 3.7 CLI

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| L01 | `run_command` | 知白名单策略 | 「请 run_command：`cmd /c echo taskly-cli-test`。」 | `run_command` | 输出或确认流 |

### 3.8 Excel（逐工具）

**文件**：`taskly-excel-test.xlsx`（相对路径 = 用户「下载」目录）。**建议按 E01→E21 顺序**。

| 编号 | 工具名 | 依赖 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| E01 | `excel_range_write` | — | 「请 excel_range_write：文件 taskly-excel-test.xlsx，Sheet1，A1，jsonData=[[\"姓名\",\"分数\",\"等级\"],[\"张三\",85,\"\"],[\"李四\",92,\"\"]]。」 | `excel_range_write` | 已写入 |
| E02 | `excel_sheets_list` | E01 | 「请 excel_sheets_list：taskly-excel-test.xlsx。」 | `excel_sheets_list` | 列出表名 |
| E03 | `excel_range_read` | E01 | 「请 excel_range_read：同一文件，Sheet1，startCell A1，endCell C3，includeFormulas false。」 | `excel_range_read` | 制表符文本 |
| E04 | `excel_formula_write` | E01 | 「请在 Sheet1 的 D2 写入公式 =B2*1.1。」 | `excel_formula_write` | 成功 |
| E05 | `excel_cells_merge` | E01 | 「请合并 Sheet1 的 A1:C1。」 | `excel_cells_merge` | 已合并 |
| E06 | `excel_cells_unmerge` | E05 | 「请取消合并 A1:C1。」 | `excel_cells_unmerge` | 已取消 |
| E07 | `excel_named_range_define` | E01 | 「请定义命名区域 ManualTestData，引用 Sheet1!A2:C3。」 | `excel_named_range_define` | 成功 |
| E08 | `excel_named_range_read` | E07 | 「请 excel_named_range_read：name=ManualTestData。」 | `excel_named_range_read` | 数据一致 |
| E09 | `excel_named_ranges_list` | E07 | 「请 excel_named_ranges_list。」 | `excel_named_ranges_list` | 含 ManualTestData |
| E10 | `excel_column_width_set` | E01 | 「请 excel_column_width_set：Sheet1，columnIndex=2，width=20。」 | `excel_column_width_set` | 成功 |
| E11 | `excel_row_height_set` | E01 | 「请 excel_row_height_set：Sheet1，rowIndex=1，height=22。」 | `excel_row_height_set` | 成功 |
| E12 | `excel_validation_set` | E01 | 「请对 Sheet1 区域 E2:E10 设 list 验证，formula1=\"优,良,差\"。」 | `excel_validation_set` | 成功 |
| E13 | `excel_validations_list` | E12 | 「请 excel_validations_list：Sheet1。」 | `excel_validations_list` | 有规则 |
| E14 | `excel_validation_clear` | E12 | 「请清除 Sheet1 的 E2:E10 验证。」 | `excel_validation_clear` | 已清除 |
| E15 | `excel_conditional_format_add` | E01 | 「请对 Sheet1 的 B2:B10 添加 between 条件格式，formula1=60，formula2=100。」 | `excel_conditional_format_add` | 成功 |
| E16 | `excel_conditional_formats_list` | E15 | 「请 excel_conditional_formats_list：Sheet1。」 | `excel_conditional_formats_list` | 有规则 |
| E17 | `excel_conditional_format_clear` | E15 | 「请清除 Sheet1 的 B2:B10 条件格式。」 | `excel_conditional_format_clear` | 已清除 |
| E18 | `excel_hyperlink_set` | E01 | 「请在 Sheet1 的 F1 设超链接 https://example.com，显示 测试链接。」 | `excel_hyperlink_set` | 成功 |
| E19 | `excel_sheet_add` | E01 | 「请添加工作表 ManualTestExtra。」 | `excel_sheet_add` | 新表存在 |
| E20 | `excel_sheet_remove` | E19 | 「请删除工作表 ManualTestExtra。」 | `excel_sheet_remove` | 已删 |
| E21 | `excel_charts_list` | — | 「请 excel_charts_list：taskly-excel-test.xlsx。」（需非空可先手工插入图表） | `excel_charts_list` | 列表或「无图表」 |

### 3.9 Word（逐工具，共 24 个函数）

**文件**：`taskly-word-test.docx`。**无表格时** `word_tables_list` / `word_tables_read` 会得到「无表格」——可先在 Word 手工插入 2×2 表再测 W02/W03，或接受「无表格」作为预期。

| 编号 | 工具名 | 依赖 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| W01 | `word_document_create` | — | 「请 word_document_create：taskly-word-test.docx，标题【手工测试】，段落用 \| 分隔：第一段 \| 第二段 \| 含【替换目标】的第三段。」 | `word_document_create` | 文件已创建 |
| W02 | `word_body_read` | W01 | 「请 word_body_read：taskly-word-test.docx，includeTables true。」 | `word_body_read` | 段落文本 |
| W03 | `word_tables_list` | 文档内有表 | 「请 word_tables_list：taskly-word-test.docx。」 | `word_tables_list` | 表数量或「无表格」 |
| W04 | `word_tables_read` | 有表 | 「请 word_tables_read：tableIndex=1。」 | `word_tables_read` | 表内容 |
| W05 | `word_find_replace` | W01 | 「请 word_find_replace：查找【替换目标】，替换为【已替换】。」 | `word_find_replace` | 成功 |
| W06 | `word_paragraphs_format` | W01 | 「请 word_paragraphs_format：第 2 段，alignment center。」 | `word_paragraphs_format` | 成功 |
| W07 | `word_text_format` | W05 | 「请 word_text_format：包含文字【已替换】，加粗、红色。」 | `word_text_format` | 成功 |
| W08 | `word_comments_list` | 可有批注 | 「请 word_comments_list。」 | `word_comments_list` | 列表或空 |
| W09 | `word_comment_add` | W05 | 「请 word_comment_add：在含【已替换】处加批注【测试批注】。」 | `word_comment_add` | 成功 |
| W10 | `word_comments_read` | W09 | 「请 word_comments_read。」 | `word_comments_read` | 批注内容 |
| W11 | `word_comments_delete` | W09 | 「请 word_comments_delete：删除刚才的批注（或指定 commentId）。」 | `word_comments_delete` | 成功 |
| W12 | `word_part_xml_read` | W01 | 「请 word_part_xml_read：partName document，maxChars 5000。」 | `word_part_xml_read` | XML 片段 |
| W13 | `word_headers_footers_list` | W01 | 「请 word_headers_footers_list。」 | `word_headers_footers_list` | 索引列表 |
| W14 | `word_header_read` | 有页眉 | 「请 word_header_read：index=1。」 | `word_header_read` | 文本 |
| W15 | `word_footer_read` | 有页脚 | 「请 word_footer_read：index=1。」 | `word_footer_read` | 文本 |
| W16 | `word_header_write` | W01 | 「请 word_header_write：index=1，文本【页眉手工测试】。」 | `word_header_write` | 成功 |
| W17 | `word_footer_write` | W01 | 「请 word_footer_write：index=1，文本【页脚手工测试】。」 | `word_footer_write` | 成功 |
| W18 | `word_bookmark_insert` | W01 | 「请 word_bookmark_insert：书签名 bm_manual，paragraphIndex=1。」 | `word_bookmark_insert` | 成功 |
| W19 | `word_bookmarks_list` | W18 | 「请 word_bookmarks_list。」 | `word_bookmarks_list` | 含 bm_manual |
| W20 | `word_bookmark_read` | W18 | 「请 word_bookmark_read：bm_manual。」 | `word_bookmark_read` | 文本 |
| W21 | `word_image_insert` | 有 taskly-img.png | 「请 word_image_insert：第 1 段后插入 taskly-img.png。」 | `word_image_insert` | 成功 |
| W22 | `word_images_list` | W21 后 | 「请 word_images_list。」 | `word_images_list` | 部件数 ≥1 |
| W23 | `word_sections_list` | W01 | 「请 word_sections_list。」 | `word_sections_list` | ≥1 节 |
| W24 | `word_hyperlink_insert` | W01 | 「请 word_hyperlink_insert：第 2 段插入链接 https://example.com，显示【点我】。」 | `word_hyperlink_insert` | 成功 |

### 3.10 Ppt（逐工具，共 14 个函数）

**文件**：`taskly-ppt-test.pptx`。**P14** 要求页上**无嵌入图片**；若已执行 P07，请再 **P01** 重建文件或另存无图页再测 duplicate。

| 编号 | 工具名 | 依赖 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| P01 | `ppt_document_create` | — | 「请 ppt_document_create：taskly-ppt-test.pptx。」 | `ppt_document_create` | 1 张灯片 |
| P02 | `ppt_slides_list` | P01 | 「请 ppt_slides_list。」 | `ppt_slides_list` | ≥1 张 |
| P03 | `ppt_slide_read` | P01 | 「请 ppt_slide_read：slideIndex=1，includeShapeDetails true。」 | `ppt_slide_read` | 文本+形状 |
| P04 | `ppt_slide_write` | P03 | 「请 ppt_slide_write：slideIndex=1，placeholderType=title，text【手工 PPT】。」 | `ppt_slide_write` | 成功 |
| P05 | `ppt_slide_insert` | P01 | 「请 ppt_slide_insert：position 取大末尾，title【第二页】，body【正文测试】。」 | `ppt_slide_insert` | 总页数 +1 |
| P06 | `ppt_slide_delete` | P05 | 「请 ppt_slide_delete：slideIndex=2。」 | `ppt_slide_delete` | 剩 1 页 |
| P07 | `ppt_slide_image_add` | 有 taskly-img.png | 「请 ppt_slide_image_add：slideIndex=1，imagePath taskly-img.png。」 | `ppt_slide_image_add` | 成功 |
| P08 | `ppt_notes_read` | P01 | 「请 ppt_notes_read：slideIndex=1。」 | `ppt_notes_read` | 文本或空 |
| P09 | `ppt_notes_write` | P01 | 「请 ppt_notes_write：slideIndex=1，【备注手工测试】。」 | `ppt_notes_write` | 成功 |
| P10 | `ppt_slides_reorder` | 至少 2 页 | 先 P05 再发：「请 ppt_slides_reorder：newOrder=2,1。」 | `ppt_slides_reorder` | 成功 |
| P11 | `ppt_table_create` | P01 | 「请 ppt_table_create：slideIndex=1，3 行 2 列。」 | `ppt_table_create` | 成功 |
| P12 | `ppt_table_write_cells` | P11 | 「请 ppt_table_write_cells：slideIndex=1，rowsCsv=`第一行左,第一行右|第二行左,第二行右`（多行用 \|，单元格用英文逗号）。」 | `ppt_table_write_cells` | 成功 |
| P13 | `ppt_hyperlink_add` | P01 | 「请 ppt_hyperlink_add：slideIndex=1，链接 https://example.com。」 | `ppt_hyperlink_add` | 成功或形状说明 |
| P14 | `ppt_slide_duplicate` | 页无嵌入图 | 「请 ppt_slide_duplicate：slideIndex=1。」若失败因图片，换无图 ppt 重试。 | `ppt_slide_duplicate` | 成功 |

### 3.11 Tavily

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| TV1 | `tavily_search` | API Key | 「请 tavily_search：查询【2025 年某项科技新闻关键词】。」 | `tavily_search` | 多条摘要 |
| TV2 | `tavily_extract` | 同上 | 「请 tavily_extract：https://example.com。」 | `tavily_extract` | 正文或失败原因 |

### 3.12 ClawhubSkill

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| CH1 | `run_clawhub_script` | 已配置技能 | 「请 run_clawhub_script：scriptName=【你的脚本名】，arguments=【空或按技能】。」 | `run_clawhub_script` | 脚本输出 |

### 3.13 Memory

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| ME1 | `save_memory` | Embedding | 「请记住：手工测试记忆前缀是 ME。」 | `save_memory` | 成功 |
| ME2 | `search_memory` | ME1 | 「我之前让你记住的前缀是什么？」 | `search_memory` | 命中 ME |

### 3.14 AccurateData

| 编号 | 工具名 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------------------------|--------------|----------|
| AD1 | `accurate_data_write` | 「accurate_data_write：id=manual-ad-1，format=md，内容 # 测试。」 | `accurate_data_write` | 成功 |
| AD2 | `accurate_data_read` | 「accurate_data_read：manual-ad-1。」 | `accurate_data_read` | 一致 |
| AD3 | `accurate_data_list` | 「accurate_data_list：前缀 manual-ad。」 | `accurate_data_list` | 含 id |
| AD4 | `accurate_data_delete` | 「accurate_data_delete：manual-ad-1。」 | `accurate_data_delete` | 已删 |

### 3.15 MeetingTranscript

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| MT1 | `meeting_transcript_meta` | 有 sessionId | 「meeting_transcript_meta：sessionId=【meeting_xxx】。」 | `meeting_transcript_meta` | totalChars |
| MT2 | `meeting_transcript_read` | 同上 | 「meeting_transcript_read：offsetChars=0，maxChars=16000，直到 hasMore=false。」 | `meeting_transcript_read` | 分块文本 |

### 3.16 ScheduledTask

| 编号 | 工具名 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------------------------|--------------|----------|
| ST1 | `scheduled_task_create` | 「scheduled_task_create：标题【手工测试】，intervalMinutes=1440。」 | `scheduled_task_create` | 有任务 id |
| ST2 | `scheduled_task_list` | 「scheduled_task_list：enabledOnly true。」 | `scheduled_task_list` | 含 ST1 |
| ST3 | `scheduled_task_read` | 「scheduled_task_read：id=【上一步】。」 | `scheduled_task_read` | 内容 |
| ST4 | `scheduled_task_update` | 「scheduled_task_update：id=【同上】，enabled=false。」 | `scheduled_task_update` | 成功 |
| ST5 | `scheduled_task_delete` | 「scheduled_task_delete：id=【同上】。」 | `scheduled_task_delete` | 已删 |

### 3.17 Context

| 编号 | 工具名 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------------------------|--------------|----------|
| CX1 | `compact_conversation` | 多轮闲聊后：「请 compact_conversation 压缩上文。」 | `compact_conversation` | 摘要 + 可继续聊 |

### 3.18 Subagent

| 编号 | 工具名 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------------------------|--------------|----------|
| SB1 | `run_subtask` | 「run_subtask：任务描述『用 3 条要点说明单元测试概念』。」 | `run_subtask` | 主对话仅短总结 |

### 3.19 CrossAgentTask

| 编号 | 工具名 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------------------------|--------------|----------|
| CA1 | `create_cross_agent_task` | 「create_cross_agent_task：targetClientType=office-word，描述【下次打开任务窗格提示手工测试】。」 | `create_cross_agent_task` | 任务已创建 |
| CA2 | `complete_cross_agent_task` | 「complete_cross_agent_task：任务 id=…，状态 done。」（需有效 id） | `complete_cross_agent_task` | 成功 |

### 3.20 Plan

| 编号 | 工具名 | 前置 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------|------------------------|--------------|----------|
| PL1 | `create_plan` | 计划模式 | 「create_plan：目标【整理下载目录测试文件清单】。」 | `create_plan` | planId |
| PL2 | `get_plan` | PL1 | 「get_plan：planId=…。」 | `get_plan` | 全文 |
| PL3 | `update_plan` | PL1 | 「update_plan：追加一行【手工备注】。」 | `update_plan` | 成功 |
| PL4 | `execute_plan_step` | PL1 | 「execute_plan_step：stepIndex=1。」 | `execute_plan_step` | 执行步骤 |
| PL5 | `complete_plan` | 步骤完成 | 「complete_plan。」 | `complete_plan` | 完成 |

### 3.21 SkillAuthor

| 编号 | 工具名 | 建议粘贴到对话框的话术 | 应核对工具名 | 预期要点 |
|------|--------|------------------------|--------------|----------|
| SK1 | `generate_user_skill` | 「generate_user_skill：goal=【Chrome 手工回归清单】，context 空。」 | `generate_user_skill` | 设置页新技能 |
| SK2 | `save_user_skill_markdown` | 模型已输出 SKILL.md 时：「save_user_skill_markdown：用上文全文。」 | `save_user_skill_markdown` | 成功 |

---

## 四、选项页（options）与配置

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| O1 | 内置插件列表 | 打开内置工具/插件区块 | 列表与后端 `/api/tools/builtin` 一致；停用某插件后对话中不可再调用 |
| O2 | 保存配置 | 修改模型/密钥/目录 | 保存成功；侧栏重连后生效 |
| O3 | MCP 服务器（外部） | 若配置外部 MCP | Chrome 会话可出现 `MCP_*` 工具；**本计划不强制逐项**（以运行时 tools/list 为准） |
| O4 | 用户技能 | 启用/禁用某 Skill | 侧栏 `@` 中 Skills 列表变化 |

---

## 五、调试与可观测（可选）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| D1 | debug-stats | 从选项页打开调试统计页（若存在） | 能看到会话、向量检索等统计，无白屏 |
| D2 | 日志 | 后端控制台/日志文件 | 出错时有异常栈或说明 |

---

## 六、内置插件与工具覆盖核对表（Chrome）

在以下插件**未**被 `DisabledBuiltInPlugins` 停用、且依赖配置已满足时，**Chrome 端应可测到**下列工具（`CurrentDocument` 整组跳过）。

| 插件 | 工具函数（KernelFunction 名） |
|------|-------------------------------|
| Browser | `highlight_webpage_text`, `add_floating_note`, `run_page_script`, `run_custom_page_script`, `capture_full_page` |
| File | `get_attachment_path`, `get_file_size`, `save_screenshot_to_downloads` |
| System | `get_current_time` |
| UserOptions | `ask_options` |
| MCP_STT | `transcribe_audio` |
| MCP_OCR | `ocr_image` |
| CLI | `run_command` |
| Excel | 见 §3.8 共 21 个 |
| Word | 见 §3.9 共 24 个 |
| Ppt | 见 §3.10 共 14 个 |
| Tavily | `tavily_search`, `tavily_extract` |
| ClawhubSkill | `run_clawhub_script` |
| Memory | `save_memory`, `search_memory` |
| AccurateData | `accurate_data_write`, `accurate_data_read`, `accurate_data_list`, `accurate_data_delete` |
| MeetingTranscript | `meeting_transcript_read`, `meeting_transcript_meta` |
| ScheduledTask | `scheduled_task_create`, `scheduled_task_list`, `scheduled_task_read`, `scheduled_task_update`, `scheduled_task_delete` |
| Context | `compact_conversation` |
| Subagent | `run_subtask` |
| CrossAgentTask | `create_cross_agent_task`, `complete_cross_agent_task` |
| Plan | `create_plan`, `get_plan`, `update_plan`, `execute_plan_step`, `complete_plan` |
| SkillAuthor | `generate_user_skill`, `save_user_skill_markdown` |

**不计入「内置插件」但会出现的**：`UserSkill_*`（用户技能 Prompt）、`MCP_*`（外部 MCP）。若需「技能」回归，在 §1.4 与 §四 O4 已覆盖；外部 MCP 按各自配置单测。

---

## 七、记录模板（测试时可复制）

**工具调用成功率**：`命中次数 / 计划用例数`，其中「命中」指日志或 UI 中可见对应 `KernelFunction` 被调用（模型未调用记为 MISS）。

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

*文档依据仓库内 `ClientTypeToolFilter`、`docs/MCP与工具清单.md` 与各 Plugin 源码整理；若后端增删工具，以代码为准更新 §六。*
