# 应用内开放给对话模型的插件列表（内部）

本文描述 **Taskly / Office Copilot 后端** 在 Semantic Kernel 上注册、供**本应用内对话 AI** 调用的插件（工具）范围。  
**不以 Cursor 或其它 IDE 的 Skill 为准**；与 Cursor `.cursor/skills` 无关。

## 权威来源与变更方式

- **注册逻辑**：`backend/ChatService.cs` 在重建 Kernel 时 `Plugins.AddFromObject` / `AddFromFunctions` / 外部 MCP 包装注册。
- **按端裁剪**：`backend/Services/ClientTypeToolFilter.cs` 决定某 `clientType` 下模型**实际可见**的插件与函数（同名插件在不同端暴露的子集不同）。
- **工具选择文案**：`backend/Services/ToolSelectionService.cs` 中 `PluginDescriptions` 供两阶段选工具时的简短说明。
- **设置页「内置工具」列表**（与 Kernel 可能略不同步）：`backend/Program.cs` 的 `GET /api/tools/builtin`（例如未列出 `Context`、`Subagent`、`CrossAgentTask`、`ScheduledTask` 时，以代码注册为准）。

若增删插件或改端侧过滤，请**先改代码**，再同步本文。

### 与代码核对（`ChatService` 主 Kernel）

- **内置插件共 22 个插件名**（下表第一节逐项列出；`ToolIndexService.BuiltinPluginNames` 与之一致）。
- **另有两类动态插件**：用户 Prompt 技能 `UserSkill_*`；配置中的外接 MCP `MCP_{McpServers.Name}`（不含内置的 `MCP_STT` / `MCP_OCR`）。
- **未发现**其它向主会话 Kernel 注册插件的路径；`SubagentPlugin` 仅调用 `ChatService.RunSubtaskAsync`，不单独挂一套插件。

---

## 一、内置插件（固定代码注册）

以下插件名即 Kernel 中的 **Plugin 名称**（工具调用时的命名空间前缀）。是否进入 Kernel 受配置项 **`disabledBuiltInPlugins`** 影响：值为**小写** id 列表，与下表「配置 id」列对应。

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
| `currentdocument` | CurrentDocument |
| `tavily` | Tavily |
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
| **File** | 附件路径、文件大小、保存截图到下载等 | |
| **System** | 当前日期与时间 | |
| **MCP_STT** | 内置语音转文字（百炼实时 ASR） | 名称含 `MCP_` 但**不是**外接 MCP；需配置实时 ASR |
| **MCP_OCR** | 内置图片 OCR | 同上，**不是**外接 MCP；需配置 OCR |
| **CurrentDocument** | 当前打开的 Word/Excel/PPT（任务窗格 RPC）：正文/选区/表格/区域/公式/幻灯片/脚本等 | 仅 Office/WPS 等连接任务窗格时可用；**Chrome 端整插件不暴露** |
| **Tavily** | 网页搜索与摘要 | 需 `tavilyApiKey` 或 `skillEnv.TAVILY_API_KEY`；无 Key 时插件仍会注册，调用可能失败 |
| **ClawhubSkill** | 运行 Clawhub 可执行技能中的脚本 | |
| **Memory** | 长期记忆 save/search | **仅当已配置 Embedding** 时注册 |
| **Context** | 对话压缩、释放上下文 | |
| **Subagent** | 同会话子代理，收回总结 | |
| **CrossAgentTask** | 跨代理任务投递与查询 | |
| **Plan** | 计划的创建/读取/执行等 | |
| **SkillAuthor** | 生成或保存用户 **SKILL.md** 技能（与设置页技能同源） | 函数：`generate_user_skill`、`save_user_skill_markdown` |
| **UserOptions** | 侧栏候选项 `ask_options` | |
| **AccurateData** | 按 id 存取大块结构化中间数据 | |
| **MeetingTranscript** | 按 Chrome 会议监听 `sessionId` 分块读取落盘转写 | |
| **ScheduledTask** | 定时任务的创建/查询/执行等 | 会话 id 以 `scheduled:` 开头时，**禁止**创建/更新/删除类工具（防套娃），见 `ClientTypeToolFilter` |

---

## 二、动态插件

### 2.1 用户 Prompt 技能 `UserSkill_*`

- 来自设置中的 **用户技能**（有 `PromptTemplate`、已启用等）。
- 插件名为 `UserSkill_{经净化的技能 id}`，每个技能通常对应一个 Prompt 型函数。
- 可执行且 id 为 `tavily` 的技能**不**再注册为 Prompt，避免与原生 Tavily 重复。

### 2.2 外接 MCP：`MCP_{配置名}`

- 来自 `AppConfig.McpServers` 中 **已启用** 的条目：`McpKernelPlugin` 动态挂载，插件名为 `MCP_` + 该条目的 **Name**。
- **内置的 `MCP_STT`、`MCP_OCR` 不走此链路**，二者为 C# 内置插件。
- 工具向量索引里，除上述两个内置外，其它 `MCP_*` 按「用户/外部」类参与索引（见 `ToolIndexService`）。

---

## 三、按客户端（clientType）可见性摘要

逻辑在 `ClientTypeToolFilter.IsAllowed`。下列「通用」集合与代码中 `IsCommonPlugin` **完全一致**。

| clientType | 模型侧要点 |
|------------|------------|
| **chrome**（默认） | **仅排除** `CurrentDocument`：**其余所有已在 Kernel 中注册的插件**（含 System、Plan、UserOptions、CLI、Browser、Office 三件套等，若未禁用）均可能暴露。 |
| **office-word** | `CurrentDocument` 中 **Word** 相关函数（及文档脚本类）+ **通用**插件：Tavily、Memory、Context、Subagent、CrossAgentTask、ClawhubSkill、AccurateData、MeetingTranscript、ScheduledTask、SkillAuthor、`UserSkill_*`、外接 `MCP_*`。**整插件不暴露**：Browser、File、CLI、Word、Excel、Ppt。 |
| **office-excel** | 同上结构，`CurrentDocument` 仅 **Excel** 相关函数 + 脚本类。 |
| **office-powerpoint** | 同上结构，`CurrentDocument` 仅 **PPT** 相关函数 + 脚本类。 |
| **wps** | 通用插件 + `CurrentDocument` 下 Word/Excel/PPT 函数及脚本的并集。 |

**重要**：**System**、**Plan**、**UserOptions** 虽在 Kernel 中注册，但 **未** 列入 `IsCommonPlugin`，因此在 **office-word / office-excel / office-powerpoint / wps** 下**不会**进入模型工具列表；仅在 **chrome**（及 `clientType` 为空时按 chrome 处理）侧可用。

---

## 四、相关文件速查

| 用途 | 路径 |
|------|------|
| Kernel 注册 | `backend/ChatService.cs` |
| 端侧过滤 | `backend/Services/ClientTypeToolFilter.cs` |
| 选工具描述 | `backend/Services/ToolSelectionService.cs` |
| 内置插件名集合（向量索引） | `backend/Services/ToolIndexService.cs` → `BuiltinPluginNames` |
| 技能撰写 | `backend/Plugins/SkillAuthorPlugin.cs` |
