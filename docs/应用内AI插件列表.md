# 应用内开放给对话模型的插件列表（内部）

本文描述 **Taskly / Office Copilot 后端** 在 **`ToolRegistry`**（MAF / MEAI `AITool`）上注册、供**本应用内对话 AI** 调用的插件（工具）范围。  
**不以 Cursor 或其它 IDE 的 Skill 为准**；与 Cursor `.cursor/skills` 无关。

## 权威来源与变更方式

- **注册逻辑**：`backend/ChatService.cs` 在 **`RebuildRuntimeAsync`** 中通过 `ToolRegistry.RegisterPluginFromObject` / MCP 包装（`McpKernelPlugin`）等注册工具；详见 [`maf-migration-baseline.md`](./maf-migration-baseline.md)。
- **按端裁剪**：`backend/Services/ClientTypeToolFilter.cs` 决定某 `clientType` 下模型**实际可见**的插件与函数（同名插件在不同端暴露的子集不同）。
- **动态工具索引**：`backend/Services/DynamicTooling/ToolCatalogIndex.cs` 从允许列表构建轻量检索；`AgentTooling` 插件提供 `search_available_tools` / `activate_tools`。
- **设置页「内置工具」列表**（与运行时注册可能略不同步）：`backend/Program.cs` 的 **`GET /api/tools/builtin`**。截至当前代码，该接口**未包含** `Pdf`、`Context`、`Subagent`、`CrossAgentTask`、`ScheduledTask` 共 **5** 项（详见本文 **§五**）；**以 `ChatService.RebuildRuntimeAsync` 注册为准**。

若增删插件或改端侧过滤，请**先改代码**，再同步本文。

### 与代码核对（主会话 `ToolRegistry`）

- **`backend/ChatService.cs` → `RebuildRuntimeAsync`**：在 **`disabledBuiltInPlugins`（小写）** 未禁用时注册内置插件实例，再 `RegisterPluginFromObject` 写入 `ToolRegistry`。
- **无条件注册（最多 21 个插件名）**：`CLI`、`Excel`、`Word`、`Ppt`、`Browser`、`File`、`System`、`MCP_STT`、`MCP_OCR`、`Pdf`、`CurrentDocument`、`ClawhubSkill`、`Context`、`Subagent`、`CrossAgentTask`、`Plan`、`SkillAuthor`、`UserOptions`、`AccurateData`、`MeetingTranscript`、`ScheduledTask`。
- **条件注册**：`Memory` — 仅当 **`_embeddingProvider.IsConfigured`**（Embedding 已配置）且未禁用 `memory` 时注册。
- 故运行时**最多 22** 个内置插件名（无 Memory 时为 **21**）。
- **另有两类动态插件**：用户 Prompt 技能 **`UserSkill_*`**；配置中的外接 MCP **`MCP_{McpServers.Name}`**（**不含**内置的 `MCP_STT` / `MCP_OCR`）。
- **未发现**其它向主会话注册工具的路径；`SubagentPlugin` 仅调用 `ChatService.RunSubtaskAsync`，不单独挂第二套 `ToolRegistry`。

---

## 一、内置插件（固定代码注册）

以下插件名即 **`ToolRegistry` 中的插件名**（工具调用时的命名空间前缀）。是否注册受配置项 **`disabledBuiltInPlugins`** 影响：值为**小写** id 列表，与下表「配置 id」列对应。

### 1.1 `disabledBuiltInPlugins` 配置 id 一览

| 配置 id（小写） | 对应插件名 |
|----------------|------------|
| `cli` | CLI |
| `excel` | Excel |
| `word` | Word |
| `ppt` | Ppt |
| `browser` | Browser |
| `file` | File |
| `system` | System |
| `mcp_stt` | MCP_STT |
| `mcp_ocr` | MCP_OCR |
| `pdf` | Pdf |
| `currentdocument` | CurrentDocument |
| `clawhub` | ClawhubSkill |
| `memory` | Memory |
| `context` | Context |
| `subagent` | Subagent |
| `crossagenttask` | CrossAgentTask |
| `plan` | Plan |
| `skillauthor` | SkillAuthor |
| `user_options` | UserOptions |
| `accuratedata` | AccurateData |
| `meetingtranscript` | MeetingTranscript |
| `scheduledtask` | ScheduledTask |

### 1.2 内置插件说明表

| 插件名 | 作用概要 | 额外条件或说明 |
|--------|----------|----------------|
| **CLI** | 白名单内系统命令 | |
| **Excel** | 读写 Excel 文件（非「当前文档」路径） | |
| **Word** | 读写 Word 文件 | |
| **Ppt** | 读写 PPT 文件 | |
| **Browser** | 网页截图、高亮、页面脚本等 | 依赖会话与 Chrome 侧能力 |
| **File** | 附件路径、文件大小、截图落盘；**文本类** `.txt` / `.md` / `.json` / `.csv` 读写（`text_file_read` / `text_file_write`） | 见下 **§1.2.1**；**Office/WPS 任务窗格会话不暴露本插件** |
| **System** | 当前日期与时间 | |
| **MCP_STT** | 内置语音转文字（百炼实时 ASR） | 名称含 `MCP_` 但**不是**外接 MCP；需配置实时 ASR |
| **MCP_OCR** | 内置图片 OCR | 同上，**不是**外接 MCP；需配置 OCR |
| **Pdf** | PDF 读文本/元数据、单文件创建、多文件合并（PdfPig + PDFsharp） | 工具：`get_pdf_text`、`get_pdf_info`、`pdf_document_create`、`pdf_merge` |
| **CurrentDocument** | 当前打开的 Word/Excel/PPT（任务窗格 RPC）：正文/选区/表格/区域/公式/幻灯片/脚本等 | **共 26 个** `ToolFunction`，见 `CurrentDocumentPlugin.cs`；仅 Office/WPS 等连接任务窗格时可用；**Chrome 端整插件不暴露** |
| **ClawhubSkill** | 运行 Clawhub 可执行技能中的脚本 | |
| **Memory** | 长期记忆 save/search | **仅当已配置 Embedding** 时注册 |
| **Context** | 对话压缩、释放上下文 | |
| **Subagent** | 同会话子代理，收回总结 | |
| **CrossAgentTask** | 跨端下发待办、执行端标记完成 | `create_cross_agent_task`、`complete_cross_agent_task`；可向在线 session 推 `cross_agent_task` / `cross_agent_task_completed` WS |
| **Plan** | 计划的创建/读取/执行等 | |
| **SkillAuthor** | 生成或保存用户 **SKILL.md** 技能（与设置页技能同源） | 函数：`generate_user_skill`、`save_user_skill_markdown` |
| **UserOptions** | 侧栏候选项 `ask_options` | |
| **AccurateData** | 按 id 存取大块结构化中间数据 | |
| **MeetingTranscript** | 按 Chrome 会议监听 `sessionId` 分块读取落盘转写 | |
| **ScheduledTask** | 定时任务的创建/查询/执行等 | 工具 5 个：`scheduled_task_create` / `list` / `read` / `update` / `delete`；会话 id 以 `scheduled:` 开头时**禁止**创建/更新/删除（防套娃），见 `ClientTypeToolFilter` |

### 1.2.1 常见办公扩展名与插件（本机路径型）

| 扩展名（示例） | 主要插件与能力 |
|----------------|----------------|
| `.docx` / `.docm` | **Word**：Open XML 读写 |
| `.xlsx` / `.xlsm` | **Excel**：Open XML 读写（**不**接受把 `.csv` 当工作簿扩展名；CSV 内容请用 **File** 文本读写或先另存为 xlsx） |
| `.pptx` / `.pptm` | **Ppt**：Open XML 读写 |
| `.pdf` | **Pdf**：`get_pdf_text`、`get_pdf_info`、`pdf_document_create`、`pdf_merge` |
| `.txt` / `.md` / `.markdown` / `.json` / `.csv` | **File**：`text_file_read`、`text_file_write`（按**纯文本**读写；不解析 CSV 为表格） |

**端侧说明**：`ClientTypeToolFilter` 对 **`office-word` / `office-excel` / `office-powerpoint` / `wps`** 仅暴露 **CurrentDocument** 与「通用」插件（Memory、Context、MCP_* 等），**不暴露 File**（也不暴露 Word/Excel/Ppt 路径插件）。因此 **`text_file_read` / `text_file_write` 主要在 Chrome 等会暴露 File 的客户端可用**；在 Office/WPS 任务窗格中处理**当前打开的文档**请用 **CurrentDocument**。

### 1.3 各内置插件 `ToolFunction` 数量（与源码一致）

下列为 `backend/Plugins` 中各插件类上 **`[ToolFunction]`** 个数（用于与 `docs/Chrome端手工测试计划.md` §六 交叉核对；Office 三件套逐工具手工测以 Chrome 文档 §3.8–§3.10 为准）。

| 插件名 | 函数数 | 代表函数名（其余见对应 `*Plugin.cs`） |
|--------|--------|----------------------------------------|
| CLI | 1 | `run_command` |
| Excel | 21 | `excel_range_write` … |
| Word | 23 | `word_document_create` … |
| Ppt | 14 | `ppt_document_create` … |
| Browser | 5 | `highlight_webpage_text`, `run_page_script`, `capture_full_page` … |
| File | 5 | `get_attachment_path`, `get_file_size`, `save_screenshot_to_downloads`, `text_file_read`, `text_file_write` |
| System | 1 | `get_current_time` |
| MCP_STT | 1 | `transcribe_audio` |
| MCP_OCR | 1 | `ocr_image` |
| Pdf | 4 | `get_pdf_text`, `get_pdf_info`, `pdf_document_create`, `pdf_merge` |
| CurrentDocument | 26 | `current_word_*`, `current_excel_*`, `current_ppt_*`, `current_run_*` |
| ClawhubSkill | 1 | `run_clawhub_script` |
| Memory | 2 | `save_memory`, `search_memory` |
| Context | 1 | `compact_conversation` |
| Subagent | 1 | `run_subtask` |
| CrossAgentTask | 2 | `create_cross_agent_task`, `complete_cross_agent_task` |
| Plan | 5 | `create_plan`, `get_plan`, `update_plan`, `execute_plan_step`, `complete_plan` |
| SkillAuthor | 2 | `generate_user_skill`, `save_user_skill_markdown` |
| UserOptions | 1 | `ask_options` |
| AccurateData | 4 | `accurate_data_write`, `read`, `list`, `delete` |
| MeetingTranscript | 2 | `meeting_transcript_read`, `meeting_transcript_meta` |
| ScheduledTask | 5 | 见上表 |

---

## 二、动态插件

### 2.1 用户 Prompt 技能 `UserSkill_*`

- 来自设置中的 **用户技能**（有 `PromptTemplate`、已启用等）。
- 插件名为 `UserSkill_{经净化的技能 id}`，每个技能通常对应一个 Prompt 型函数。

### 2.2 外接 MCP：`MCP_{配置名}`

- 来自 `AppConfig.McpServers` 中 **已启用** 的条目：`McpKernelPlugin` 动态挂载，插件名为 `MCP_` + 该条目的 **Name**。
- **内置的 `MCP_STT`、`MCP_OCR` 不走此链路**，二者为 C# 内置插件。

---

## 三、按客户端（clientType）可见性摘要

逻辑在 `ClientTypeToolFilter.IsAllowed`。下列「通用」集合与代码中 `IsCommonPlugin` **完全一致**。

| clientType | 模型侧要点 |
|------------|------------|
| **chrome**（默认） | **仅排除** `CurrentDocument`：**其余所有已在 ToolRegistry 中注册的插件**（含 **Pdf**、System、Plan、UserOptions、CLI、Browser、File、Office 三件套、Context、Subagent、CrossAgentTask、ScheduledTask 等，若未禁用）均可能暴露。 |
| **office-word** | `CurrentDocument` 中 **Word** 相关函数（及文档脚本类）+ **通用**插件：Memory、Context、Subagent、CrossAgentTask、ClawhubSkill、AccurateData、MeetingTranscript、ScheduledTask、SkillAuthor、`UserSkill_*`、外接 `MCP_*`。**整插件不暴露**：Browser、File、CLI、Word、Excel、Ppt。 |
| **office-excel** | 同上结构，`CurrentDocument` 仅 **Excel** 相关函数 + 脚本类。 |
| **office-powerpoint** | 同上结构，`CurrentDocument` 仅 **PPT** 相关函数 + 脚本类。 |
| **wps** | 通用插件 + `CurrentDocument` 下 Word/Excel/PPT 函数及脚本的并集。 |

**重要**：**System**、**Plan**、**UserOptions** 虽在注册表中存在，但 **未** 列入 `IsCommonPlugin`，因此在 **office-word / office-excel / office-powerpoint / wps** 下**不会**进入模型工具列表；仅在 **chrome**（及 `clientType` 为空时按 chrome 处理）侧可用。

---

## 四、相关文件速查

| 用途 | 路径 |
|------|------|
| 工具注册 / 运行时重建 | `backend/ChatService.cs`（`RebuildRuntimeAsync`） |
| 内置插件清单（HTTP，可能与运行时略不同步） | `backend/Program.cs`（`GET /api/tools/builtin`） |
| 端侧过滤 | `backend/Services/ClientTypeToolFilter.cs` |
| 动态工具 / 检索 | `backend/Services/DynamicTooling/`、`backend/Plugins/AgentToolingPlugin.cs` |
| 技能撰写 | `backend/Plugins/SkillAuthorPlugin.cs` |
| Chrome 端逐工具手工测 | `docs/Chrome端手工测试计划.md` |

---

## 五、`GET /api/tools/builtin` 与 `ToolRegistry` 差异（核对用）

`Program.cs` 中 **`GET /api/tools/builtin`** 当前返回 **17** 条 `BuiltInPluginInfo`（`Browser` … `SkillAuthor`），**缺少**下列 **5** 个已在 `RebuildRuntimeAsync` 中注册的插件（选项页若仅依赖该接口展示「全部内置」，会漏项）：

| 缺失项（插件名） | 配置 id（小写） |
|------------------|-----------------|
| Pdf | `pdf` |
| Context | `context` |
| Subagent | `subagent` |
| CrossAgentTask | `crossagenttask` |
| ScheduledTask | `scheduledtask` |

**结论**：核对「是否启用/禁用某内置插件」时，以 **`ChatService.RebuildRuntimeAsync` + 本文 §1.1** 为准；若希望 HTTP 列表与运行时完全一致，需在 `Program.cs` 的 `builtIn` 列表中补全上表 5 项（并维护 Description 文案）。
