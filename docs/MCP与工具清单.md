# 项目 MCP 与工具清单

本文档列出本仓库中**所有端的 MCP 工具与后端内置工具**，并附中文介绍。  
外部 MCP（通过设置中「MCP 服务器」配置接入）的工具由各 MCP 在运行时提供，不在此列出。

---

## 一、本仓库实现的 MCP 服务器（Cursor/VSCode 端）

在 `.vscode/mcp.json` 中配置的、**本项目自己实现的** MCP 为 **SalesDbMcp**（`sales-db-mcp` 项目）。  
连接串通过 `SalesDb:ConnectionString` 或环境变量 `SALES_DB_CONNECTION_STRING` 配置。

| 工具名 | 中文介绍 |
|--------|----------|
| `SalesDbHealthAsync` | 检查销售数据库连接是否正常，返回成功或错误信息。 |
| `SalesDbQueryAsync` | 对销售数据库执行只读 SELECT 查询，仅允许 SELECT 语句，返回结果行为 JSON 友好文本。 |
| `SalesDbListTablesAsync` | 列出销售库中的表与视图名称，可按 schema（如 dbo）过滤。 |
| `SalesDbGetSchemaAsync` | 获取指定表或视图的列名、类型及主键等信息，支持 TABLE_SCHEMA.TABLE_NAME 或 TABLE_NAME 形式。 |

---

## 二、后端内置插件与工具（Office Copilot Server）

后端通过 Kernel 注册多类内置插件，每类插件下有多条工具函数。以下按**插件**分组，每条工具给出**工具名**与**中文介绍**。  
各插件可通过配置中的 `DisabledBuiltInPlugins`（插件 ID 不区分大小写）关闭，关闭后该插件及其工具不会注册。

### 1. Browser（浏览器）

| 工具名 | 中文介绍 |
|--------|----------|
| `highlight_webpage_text` | 在用户当前激活的网页上高亮指定文本。 |
| `add_floating_note` | 在用户当前网页上添加可拖动的浮动便签，内容与标题可自定义，可锚定在指定文字上方。 |
| `run_page_script` | 在用户当前浏览器标签页的页面上下文中执行预定义脚本（仅支持白名单内 scriptId），如滚动、获取可见文本等。 |
| `capture_full_page` | 对用户当前浏览器标签页进行整页截图，返回截图引用（如 screenshot:xxx），需再通过 save_screenshot_to_downloads 保存到下载目录。 |

### 2. File（文件）

| 工具名 | 中文介绍 |
|--------|----------|
| `get_attachment_path` | 根据用户消息中的附件引用（如 attachment:xxx）返回本机临时文件路径，供 OCR 等需要路径的工具使用；引用无效或过期时返回错误提示。 |
| `get_file_size` | 获取指定本地文件的字节数与人类可读大小。**作用**：在决定是否将文件内容纳入 AI 上下文、或选用读文件/OCR/STT 等工具前，先判断文件大小，避免大文件占满上下文或盲目读取。路径可来自 get_attachment_path。 |
| `save_screenshot_to_downloads` | 将整页截图保存到用户下载文件夹；需传入由 capture_full_page 返回的截图引用，可选指定不含扩展名的文件名。 |

### 3. System（系统）

| 工具名 | 中文介绍 |
|--------|----------|
| `get_current_time` | 获取当前日期与时间（UTC 与本地）。**作用**：用户问「今天几号」「现在几点」「本周一」等时间相关问题时调用，使回答基于真实当前时间而非训练数据或幻觉日期。 |

### 4. UserOptions（候选选项确认）

| 工具名 | 中文介绍 |
|--------|----------|
| `ask_options` | 当模型需要你从候选项中分步确认选择时调用。`stepsJson` 为一个 JSON 数组字符串：每个元素包含 `stepId`、`question`、`options`（`optionId`、`label`）。调用后会等待你在侧边栏逐步选择每一步的一个选项，并在最后一次性返回所有 selections（形如 `stepId -> optionId`）。 |

### 4. MCP_STT（语音转文字，内置）

以 MCP 风格命名的内置插件，供主模型按需调用；需在「模型设置」中配置语音转文字（如 Whisper）或回退使用当前 AI 模型 endpoint/apiKey。

| 工具名 | 中文介绍 |
|--------|----------|
| `transcribe_audio` | 将指定路径的音频文件转成文字；支持 mp3、wav、m4a、webm 等，单文件不超过 25MB；可选 language 参数（如 zh、en），留空自动检测。 |

### 5. MCP_OCR（图片转文字，内置）

以 MCP 风格命名的内置插件，供主模型按需调用；需在「模型设置」中配置 OCR 模型的接口地址与 API Key，不配置则此工具不可用。

| 工具名 | 中文介绍 |
|--------|----------|
| `ocr_image` | 从指定路径的图片文件中提取文字；支持 png、jpg、jpeg、gif、bmp、webp、tiff 等，单文件不超过 20MB；路径可来自 get_attachment_path。 |

### 6. CLI（命令行）

| 工具名 | 中文介绍 |
|--------|----------|
| `run_command` | 在用户本机（后端所在机器）的 CMD 中执行一条命令并返回输出，适用于查看文件列表、系统信息、执行脚本等。当按端 `CliRunModeByClient` 为 `RunEverything` 时不校验并直接执行；为 `UseAllowList` 时：白名单内命令不弹确认，非白名单命令需用户确认后才执行；为 `AskEverytime` 时：每次执行前都需用户确认。默认白名单命令包含 `dir/echo/type/ping/systeminfo/ipconfig`（可在设置里按端配置或追加）。 |

### 7. Excel

| 工具名 | 中文介绍 |
|--------|----------|
| `excel_sheets_list` | 列出 Excel 工作簿中所有工作表名称；filePath 支持环境变量与相对路径（解析到下载目录）。 |
| `excel_range_read` | 读取指定工作表区域的值或公式，返回制表符分隔文本；可设 maxRows、endCell 控制大文件内存，includeFormulas 为 true 时返回公式。 |
| `excel_range_write` | 向指定工作表和起始单元格写入 JSON 二维数组数据；文件不存在则创建。 |
| `excel_formula_write` | 向指定单元格写入公式字符串。 |
| `excel_cells_merge` | 合并指定矩形区域单元格，仅左上角保留内容。 |
| `excel_cells_unmerge` | 取消指定区域的合并。 |
| `excel_named_ranges_list` | 列出工作簿中所有命名区域（名称与引用）。 |
| `excel_named_range_read` | 按命名区域名称读取其引用范围内的数据（值）。 |
| `excel_named_range_define` | 定义或覆盖一个命名区域，引用给定工作表中的区域。 |
| `excel_column_width_set` | 设置指定列的列宽（字符宽度数）。 |
| `excel_row_height_set` | 设置指定行的行高（磅值）。 |
| `excel_validations_list` | 列出工作表中所有数据验证规则。 |
| `excel_validation_set` | 为区域设置数据验证（类型如 list、whole、decimal、textLength 等）。 |
| `excel_validation_clear` | 清除指定区域的数据验证。 |
| `excel_conditional_formats_list` | 列出工作表中所有条件格式规则。 |
| `excel_conditional_format_add` | 为区域添加条件格式（如单元格值介于两数之间）。 |
| `excel_conditional_format_clear` | 清除指定区域的条件格式。 |
| `excel_hyperlink_set` | 为单元格设置超链接。 |
| `excel_sheet_add` | 在工作簿末尾添加新工作表。 |
| `excel_sheet_remove` | 按名称删除工作表。 |
| `excel_charts_list` | 列出工作簿中各工作表中的图表。 |

### 8. Word

| 工具名 | 中文介绍 |
|--------|----------|
| `word_body_read` | 读取 Word 文档正文（段落，可选含表格），支持段落范围与长度截断。 |
| `word_tables_list` | 列出文档中所有表格的索引与简要信息。 |
| `word_tables_read` | 读取一个或全部表格内容，制表符分隔；tableIndex 为 0 时读全部。 |
| `word_document_create` | 创建新 Word 文档并写入标题与段落；文件已存在则覆盖。 |
| `word_find_replace` | 在文档中查找并替换文本。 |
| `word_paragraphs_format` | 对指定段落设置对齐、样式、段前段后间距（alignment: left/center/right/justify）。 |
| `word_text_format` | 对包含指定文字的所有 Run 设置字体、字号、加粗、斜体、颜色。 |
| `word_comments_list` | 列出文档中所有批注的 Id、作者与摘要。 |
| `word_comments_read` | 读取所有批注内容，含被批注原文（若有）。 |
| `word_comment_add` | 在包含指定文字的首次出现处插入批注。 |
| `word_comments_delete` | 按批注 Id 删除批注，或删除全部（commentId 留空且 deleteAll 为 true）。 |
| `word_part_xml_read` | 读取文档指定部件的原始 XML（document、comments、styles 等），可限制 maxChars。 |
| `word_headers_footers_list` | 按节列出文档中的页眉页脚（索引与类型）。 |
| `word_header_read` | 读取指定索引的页眉文本（index 从 1 开始）。 |
| `word_footer_read` | 读取指定索引的页脚文本（index 从 1 开始）。 |
| `word_header_write` | 写入或替换指定页眉的文本；若不存在则创建。 |
| `word_footer_write` | 写入或替换指定页脚的文本。 |
| `word_bookmarks_list` | 列出文档中所有书签名称。 |
| `word_bookmark_read` | 读取书签所标记范围的文本。 |
| `word_bookmark_insert` | 在指定位置插入书签（paragraphIndex 从 1 开始，表示在该段末尾插入）。 |
| `word_images_list` | 列出文档中图片部件数量与关系 Id。 |
| `word_image_insert` | 在指定段落后插入图片；imagePath 为本地图片文件路径。 |
| `word_sections_list` | 列出文档中的节（SectionProperties 数量）。 |
| `word_hyperlink_insert` | 在指定段落插入超链接文本。 |

### 9. Ppt（PPT，Chrome/后端 OpenXml 文件路径）

勿用 shell 重定向伪造 `.pptx`；新建请用 `ppt_document_create`。

| 工具名 | 中文介绍 |
|--------|----------|
| `ppt_document_create` | 创建新的空白演示文稿（.pptx/.pptm），已存在则覆盖；至少 1 张幻灯片。 |
| `ppt_slides_list` | 列出所有幻灯片序号与文本预览（SlideIdList 顺序）；filePath 支持环境变量与相对路径。 |
| `ppt_slide_read` | 读取指定页全文；可选 `includeShapeDetails` 附加带文本形状编号列表（供 `shapeIndex`）。 |
| `ppt_slide_write` | 写入文本：优先 `shapeIndex`/`shapeName`，否则 `placeholderType`（title/body/subtitle/ctrTitle）。 |
| `ppt_slide_insert` | 插入新幻灯片（标题/正文占位）；`position`：0=最前，k=第 k 页之后，≥页数=末尾。 |
| `ppt_slide_delete` | 删除指定序号幻灯片。 |
| `ppt_slide_image_add` | 向指定页插入本地图片（PNG/JPEG 等），可设 EMU 位置与大小。 |
| `ppt_notes_read` / `ppt_notes_write` | 读/写演讲者备注（无则创建备注页）。 |
| `ppt_slides_reorder` | 全量重排：`newOrder` 如 `2,3,1` 表示新的播放顺序。 |
| `ppt_table_create` | 在指定页添加简单表格（行列上限内）。 |
| `ppt_table_write_cells` | 向该页首张表格填字：`行|行`，行内单元格用英文逗号分隔。 |
| `ppt_hyperlink_add` | 为某文本形状首个 Run 设置外部 URL 点击链接。 |
| `ppt_slide_duplicate` | 复制幻灯片到紧邻其后（**无嵌入图片**的页才可复制）。 |

### 10. CurrentDocument（当前文档，任务窗格）

仅在用户从 Word/Excel/PowerPoint/WPS 任务窗格连接时可用。

| 工具名 | 中文介绍 |
|--------|----------|
| `current_word_insert_text` | 在当前打开的 Word 文档末尾插入一段文字。 |
| `current_word_read_body` | 读取当前打开的 Word 文档正文（可选截断长度）。 |
| `current_word_read_selection` | 读取当前 Word 文档中用户选中的文本。 |
| `current_word_insert_table` | 在当前 Word 文档中插入表格（可指定行数、列数、内容与插入位置）。 |
| `current_word_search_replace` | 在当前 Word 文档正文或选区内查找并替换文本。 |
| `current_excel_read_range` | 读取当前打开的 Excel 工作表中指定区域的数据。 |
| `current_excel_write_range` | 向当前 Excel 工作表的指定区域写入数据（二维数组 JSON）。 |
| `current_excel_list_sheets` | 列出当前打开的 Excel 工作簿中所有工作表名称。 |
| `current_excel_get_used_range` | 获取当前工作表中已使用区域的地址与数据。 |
| `current_excel_read_formulas` | 读取当前 Excel 工作表中指定区域的公式。 |
| `current_excel_write_formulas` | 向当前 Excel 工作表的指定区域写入公式（二维数组 JSON）。 |
| `current_ppt_slides_list` | 列出当前打开的 PPT 演示文稿中所有幻灯片（按播放顺序）。 |
| `current_ppt_slide_read` | 读取指定幻灯片文本；可选 `includeShapeDetails` 附加形状编号。 |
| `current_ppt_slide_write` | 写入文本（slideIndex、placeholderType、text；可选 shapeIndex、shapeName）。 |
| `current_ppt_slide_insert` | 插入新幻灯片（可选 position、titleText、bodyText）。 |
| `current_ppt_slide_delete` | 删除指定序号的幻灯片。 |
| `current_ppt_slide_image_add` | 插入图片（任务窗格端可能返回「请用 Chrome+文件路径」）。 |
| `current_ppt_notes_read` / `current_ppt_notes_write` | 读/写备注（任务窗格端可能未实现）。 |
| `current_ppt_slides_reorder` | 重排幻灯片（任务窗格端可能未实现）。 |
| `current_ppt_table_create` / `current_ppt_table_write_cells` | 表格（任务窗格端可能未实现）。 |
| `current_ppt_hyperlink_add` | 超链接（任务窗格端可能未实现）。 |
| `current_ppt_slide_duplicate` | 复制幻灯片（任务窗格端可能未实现）。 |
| `current_run_document_script` | 在当前打开的 Office/WPS 文档环境中执行预定义脚本（仅支持白名单内 scriptId）。 |

### 11. Tavily

需配置 TAVILY_API_KEY。  

| 工具名 | 中文介绍 |
|--------|----------|
| `tavily_search` | 使用 Tavily API 进行网页搜索，返回简洁相关结果，适合 AI 摘要；适用于查实时信息、新闻或网络资料。 |
| `tavily_extract` | 从指定 URL 提取正文内容，适用于需要阅读网页全文时。 |

### 12. ClawhubSkill（Clawhub 可执行技能）

| 工具名 | 中文介绍 |
|--------|----------|
| `run_clawhub_script` | 在后端所在机器的 Node 进程中运行 Clawhub 可执行技能目录 scripts/ 下的脚本；scriptName 为脚本名（不含扩展名），arguments 为空格分隔的参数字符串。 |

### 13. Memory（长期记忆）

需配置 Embedding 模型。  

| 工具名 | 中文介绍 |
|--------|----------|
| `save_memory` | 将用户或对话中的一条重要信息保存为长期记忆，便于后续检索；saveToShared 为 true 时写入共享记忆，其他端（如 Word/Chrome）也可检索到。 |
| `search_memory` | 根据查询从长期记忆中检索相关条目（本会话 + 共享记忆），返回与当前问题最相关的记忆列表，结果会标明来自本会话或共享。 |

### 14. AccurateData（准确数据）

以文件形式持久化与按 id 读取规范数据，目录由设置中「准确数据目录」或配置 AccurateDataDirectory 指定。  

| 工具名 | 中文介绍 |
|--------|----------|
| `accurate_data_write` | 写入或覆盖一条准确数据；id 为唯一键（字母数字、短横线、下划线），format 为 md 或 json。 |
| `accurate_data_read` | 按 id 读取一条准确数据条目的原始内容。 |
| `accurate_data_list` | 列出准确数据条目，可按 id 前缀过滤并限制数量，返回每条 id 与格式。 |
| `accurate_data_delete` | 按 id 删除一条准确数据（同时删除内容与 meta）。 |

### 15. ScheduledTask（定时任务）

供 AI 创建与管理定时任务（.task.md + meta）；到点后由后台将任务内容发给 AI 执行。目录由「定时任务目录」或配置 ScheduledTasksDirectory 指定。  

| 工具名 | 中文介绍 |
|--------|----------|
| `scheduled_task_create` | 创建定时任务；支持 cron 或 interval，cron 需提供 cronExpression（如 0 9 * * 1-5），interval 需提供 intervalMinutes。 |
| `scheduled_task_list` | 列出定时任务，返回 id、title、nextRunAt、enabled；可选仅返回已启用任务。 |
| `scheduled_task_read` | 按 id 读取一条定时任务的内容与 meta。 |
| `scheduled_task_update` | 更新定时任务；可选参数不传则保持原值。 |
| `scheduled_task_delete` | 删除定时任务（移除 .task.md 与 .meta.json）。 |

### 16. Context（上下文压缩）

| 工具名 | 中文介绍 |
|--------|----------|
| `compact_conversation` | 当对话较长且要开始全新任务、或已给出最终结论不再需要此前详细内容时，可调用此工具压缩对话，将较早轮次合并为摘要以释放上下文。 |

### 17. Subagent（子代理）

| 工具名 | 中文介绍 |
|--------|----------|
| `run_subtask` | 将相对独立、多步骤或会产出大量中间结果的任务交给子代理；子代理在隔离上下文中完成并只返回最终总结，主对话仅收到该总结；taskDescription 需写清要完成的具体目标。 |

### 18. CrossAgentTask（跨端任务）

| 工具名 | 中文介绍 |
|--------|----------|
| `create_cross_agent_task` | 当用户要求「让 Word/Chrome/Excel/WPS/PowerPoint 的 Agent 做某事」时调用；创建一条发给目标端的待办，目标端在下次对话时会看到并执行；targetClientType 取 office-word、chrome、office-excel、office-powerpoint、wps 之一。 |
| `complete_cross_agent_task` | 当本端完成了一条来自其他端的待办任务后调用，将任务标记为 done 或 failed，并可选写入结果摘要。 |

### 19. Plan（计划）

| 工具名 | 中文介绍 |
|--------|----------|
| `create_plan` | 根据用户目标生成一份实现计划（Markdown）并保存到计划库；仅在「计划模式」下使用，生成后返回 planId 供后续查看或执行。 |
| `get_plan` | 根据 planId 读取计划全文与元数据，用于查看或作为执行上下文。 |
| `update_plan` | 更新计划内容（用户编辑后同步到后端）。 |
| `execute_plan_step` | 读取计划中指定步骤的内容并执行该步骤（调用现有工具完成）；stepIndex 从 1 开始。 |
| `complete_plan` | 将计划标记为已完成。在全部步骤执行完毕后调用。 |

---

## 三、外部 MCP（后端可接入）

在**设置**中通过「MCP 服务器」配置添加的 MCP（如 OCR、其他数据库等），其**工具名与数量由该 MCP 在运行时的 `tools/list` 返回**，本仓库代码中无固定列表。后端会以 `MCP_{配置的 Name}` 作为插件名注册到 Kernel，在工具选择中归为「外部 / MCP 工具」。

---

*文档由项目代码与注释整理，如有变更以代码为准。*
