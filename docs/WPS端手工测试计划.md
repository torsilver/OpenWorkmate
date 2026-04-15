# WPS 加载项手工测试计划

> **范围**：**金山 WPS** 内通过 **[wps-addin-new/](../wps-addin-new/)** 任务窗格连接本机 **Office Copilot Server**（`clientType=wps`）。  
> **不包含**：Chrome 扩展独有能力（**`Browser`**、侧栏会议监听等）。**`File` / `CLI` / `System` / `UserOptions`** 在 `clientType=wps` 下与 `IsCommonPlugin` 一致**会暴露**（见 `ClientTypeToolFilter`）；`get_attachment_path` 仍依赖 Chrome 侧附件落盘链，WPS 上可能不适用。本文也不替代 `docs/Chrome端手工测试计划.md`。  
> **权威对照**：端侧 RPC 与宿主守卫见 `wps-addin-new/src/composables/useCopilot.js`（`handleRpcRequest`）、`wps-addin-new/src/wps-rpc/hostKind.js`；工具名与过滤见 `backend/Plugins/CurrentDocumentPlugin.cs`、`backend/Services/ClientTypeToolFilter.cs`；WPS 与 `wpsHostKind` 见 `docs/应用内AI插件列表.md` §三、`docs/WPS插件调试指南.md` §3.1。

**通过标准（每条用例）**：行为与下表「预期」一致；失败时工具返回或 UI 中可见**明确原因**（含 RPC `error` 回包、后端 `message`），符合项目错误可见性约定。

**调试方式**：以 **`npx wpsjs debug`**（在 `wps-addin-new` 目录）为准，见 `docs/WPS插件调试指南.md`。

---

## 零、测试前环境

| 序号 | 检查项 | 说明 |
|------|--------|------|
| W0 | 后端已启动 | 本机 `OfficeCopilot.Server` 可连；任务窗格能完成握手（见 W1） |
| W1 | 加载项已加载 | `wpsjs debug` 拉起后，在 **WPS 文字 / 表格 / 演示** 中打开 **Office Copilot** 任务窗格 |
| W2 | 模型与密钥 | 与扩展/选项页或环境变量一致，请求不被 401 |
| W3 | 动态工具（若测 §八） | `AppConfig.contextWindow.dynamicTooling.enabled` 为 **true**（默认）；首轮为 bootstrap，业务工具需 **`search_available_tools` → `activate_tools`** |
| W4 | Pdf / Memory / MCP | 按需：Pdf 读写**服务端进程可见路径**；Memory 需 Embedding；外接 MCP 需在设置中启用 |

---

## 一、连接、会话与 `wpsHostKind`

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| C1 | WebSocket 连通 | 打开任务窗格，确认已连上后台 | 可发送消息并收到流式回复；断线时有重连或明确提示 |
| C2 | `set_context` | 打开/切换文档后观察（或抓包 WS） | 负载含 **`type: set_context`** 与 **`wpsHostKind`**（`word` / `et` / `wpp` / `unknown` / `none` 等，与 `getWpsHostKind` 一致） |
| C3 | 宿主与工具收窄 | 在 **WPS 文字** 已打开文档、`wpsHostKind=word` 时，让模型 **仅** 用动态工具流程尝试激活 **`current_excel_read_range`** | **`activate_tools` 应失败或不可选**：当前轮允许列表中不含跨宿主 Excel 工具（与 `ClientTypeToolFilter` 收紧一致） |
| C4 | 未收紧行为 | 首轮未上报或 `unknown`/`none` 时（可短暂断连再连复现） | **可**在索引中同时检索到 Word/Excel/PPT 类 `current_*`（略宽策略）；具体以当前会话存储为准 |

---

## 二、与其它端的差异：负向 + `IsCommonPlugin` 正向抽样

`wps` 与 `office-*` 相同：先放行 **`IsCommonPlugin`**（含 **File、CLI、System、UserOptions** 等），再 **Pdf**，再 **`CurrentDocument`**（及 `wpsHostKind` 收窄）。与早期实现/旧文档「任务窗格不暴露 File」可能不一致；**以当前 `ClientTypeToolFilter.IsCommonPlugin` 源码为准**（可用 `git log -p -- backend/Services/ClientTypeToolFilter.cs` 查看演进）。

### 2.1 负向（WPS 仍不具备）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| N1 | 无浏览器工具 | 要求「截图当前网页」「run_builtin_page_script」 | 模型侧**不应**成功调用 `Browser.*`；若误调应返回不可用说明 |
| NP1 | 无 **Plan** 插件 | 要求 `create_plan` / `get_plan` / `execute_plan_step` | **Plan** 未列入 `IsCommonPlugin`，`office-*`/`wps` 下**不在**允许列表；`activate_tools` 无法激活 |
| N4 | 无会议转写链路 | 要求 `meeting_transcript_read` | 工具可能仍在列表中，但 **WPS 无 Chrome 会议监听**；失败或空结果须有清晰说明 |
| N5 | 无 `run_browser_subtask` | 尝试子任务里跑浏览器 | `Subagent.run_browser_subtask` **仅 Chrome**；应被拒绝或不可见 |

### 2.2 正向（与 `IsCommonPlugin` 一致，建议各抽 1 条）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| CF1 | **File**：`text_file_read` | 提供**后端进程可读**的绝对路径（如本机某 `.txt`） | 返回文件内容或明确权限/路径错误 |
| CF2 | **System**：`get_current_time` | 「现在几点」并允许调用工具 | 返回服务端当前时间 |
| CF3 | **CLI**：`run_command` | 使用配置白名单内的一条只读命令（注意 HITL） | 与 Chrome 行为一致：确认后执行或拒绝说明 |
| CF4 | **UserOptions**：`ask_options` | 若业务会触发侧栏选项 | 任务窗格能展示选项并回传（与实现对齐） |
| CF5 | **get_attachment_path** | 在 **仅 WPS**、无 Chrome 附件链时测 | 可预期失败或说明「无附件」；**不作为**「WPS 无 File」依据 |

---

## 三、宿主守卫（RPC 方法前缀与当前组件）

任务窗格在 `handleRpcRequest` 内对 **`word_*` / `excel_*` / `ppt_*`** 分别校验当前宿主为 **文字 / 表格 / 演示**；错误文案含「仅能在 WPS 文字/表格/演示中执行」类提示（`assertWpsHost`）。

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| G1 | 错宿主调 Word RPC | 在 **WPS 表格** 中连接，用户话术中强制模型调用 **`current_word_insert_text`**（或等价 RPC） | RPC 返回 **error**：仅能在 WPS **文字** 中执行 |
| G2 | 错宿主调 Excel RPC | 在 **WPS 文字** 中连接，调用 **`current_excel_read_range`** | 返回 **error**：仅能在 WPS **表格** 中执行 |
| G3 | 错宿主调 PPT RPC | 在 **WPS 文字** 中连接，调用 **`current_ppt_slides_list`** | 返回 **error**：仅能在 WPS **演示** 中执行 |

---

## 四、WPS 文字：`CurrentDocument`（Word 类）

在 **WPS 文字** 打开任意 `.wps`/`.docx`，任务窗格已连接。建议准备含数段正文的文档；**插入类**用例会改文档，可用副本。

| 序号 | 工具（裸名） | 建议用户话术 / 模型行为 | 预期 |
|------|----------------|---------------------------|------|
| WD1 | `current_word_read_body` | 「读取当前文档正文，不要改文件」 | 返回正文文本或截断说明；无「假成功」 |
| WD2 | `current_word_read_selection` | 先选中一段文字，再「读取当前选区」 | 返回选中文本或 `(无选区)`；若 WPS API 不支持应有说明 |
| WD3 | `current_word_insert_text` | 「在文末插入一行：【手工测试 WD3】」 | 文档末尾出现该句；工具返回成功类文案 |
| WD4 | `current_word_insert_table` | 「插入 2 行 3 列表格，表头为 A/B/C」 | 表格出现且工具返回成功（参数形状见 `wordTableRpc.js` 与插件说明） |
| WD5 | `current_word_search_replace` | 「把文档中某个唯一词替换为 XXX（可先手写唯一词）」 | 替换生效；`replaceAll` 行为与插件一致 |

---

## 五、WPS 表格：`CurrentDocument`（Excel 类）

在 **WPS 表格** 打开工作簿，在 `Sheet1` 的 `A1:C3` 填入简单数据（如 1、2、3）。

| 序号 | 工具 | 建议操作 | 预期 |
|------|------|----------|------|
| ET1 | `current_excel_read_range` | 「读取当前表 A1:C3」 | 返回 JSON/表格化字符串，与单元格一致 |
| ET2 | `current_excel_write_range` | 「把 B2 写成 99」或二维数组写入小区域 | 单元格更新；返回成功说明 |
| ET3 | `current_excel_list_sheets` | 「列出所有工作表名称」 | 返回表名列表 |
| ET4 | `current_excel_get_used_range` | 「当前表已用区域」 | 返回区域地址或说明 |
| ET5 | `current_excel_read_formulas` | 在某格写 `=1+1` 后读取公式 | 返回公式文本 |
| ET6 | `current_excel_write_formulas` | 「将 A4 公式设为 =SUM(A1:A3)」 | 公式写入且计算结果合理 |

实现锚点：`wps-rpc/excelRpc.js`（`runWpsExcelRpc`）。

---

## 六、WPS 演示：`CurrentDocument`（PPT 类）

在 **WPS 演示** 打开至少 2 张幻灯片的 `.pptx`。部分用例会改稿，建议用副本。

| 序号 | 工具 | 建议操作 | 预期 |
|------|------|----------|------|
| PP1 | `current_ppt_slides_list` | 「列出当前演示文稿所有幻灯片标题/预览」 | 返回张数与每页摘要文本 |
| PP2 | `current_ppt_slide_read` | 「读取第 1 张幻灯片文本」 | 返回该页文本；`slideIndex` 越界时有明确错误 |
| PP3 | `current_ppt_slide_write` | 「把第 1 页标题占位符改成【测试PP3】」 | 页内文本更新 |
| PP4 | `current_ppt_slide_insert` | 「在最后插入一页，标题为 Hi」 | 幻灯片数 +1 |
| PP5 | `current_ppt_slide_delete` | 在副本上删除最后一页 | 张数 -1 或返回不支持说明 |
| PP6 | `current_ppt_document_create` | 「新建并保存到 `D:\Temp\test-wps-create.pptx`」（路径须本机可写且 WPS 可 `SaveAs`） | 文件落盘或返回路径/权限错误 |
| PP7 | 其它 `current_ppt_*` | 按需抽测 `slide_image_add`（路径或 base64）、`notes_read/write`、`slides_reorder`、`table_create`、`table_write_cells`、`hyperlink_add`、`slide_duplicate` | 与 `useCopilot.js` 分支一致；失败时含可操作原因（如 base64 写临时文件失败会提示用路径） |

---

## 七、预置文档脚本：`current_run_document_script`

仅允许 **`DOCUMENT_SCRIPTS`** 中已注册的 `scriptId`（`useCopilot.js`）。当前白名单包括：

| scriptId | 适用宿主 | 建议测法 |
|----------|----------|----------|
| `word_read_selection` | 文字 | 与 WD2 类似，通过 **scriptId** 路径调用 |
| `wps_doc_meta` | 任意 | 应返回含 `clientType: wps` 与当前文档/工作簿/演示名 |
| `wps_word_body_preview` | 文字 | 返回带长度上限的正文摘录 |
| `wps_ppt_slide_glance` | 演示 | 返回当前页幻灯片快览 |

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| S1 | 合法 `scriptId` | 「调用 current_run_document_script，scriptId 为 wps_doc_meta」 | 返回元信息字符串 |
| S2 | 非法 `scriptId` | `scriptId=not_registered` | RPC 错误：`未知或未注册的脚本 ID` |

---

## 八、自定义脚本与 HITL：`current_run_custom_document_script`

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| H1 | 需确认 | 让模型发起一段无害脚本（如 `return 1+1`） | 任务窗格出现 **确认** 流程；拒绝/超时行为符合 `SecurityPipeline` |
| H2 | 执行结果 | 用户确认后 | 返回 `2` 或等价字符串；拒绝则不执行且无静默成功 |

---

## 九、动态工具（可选但推荐）

| 序号 | 场景 | 操作 | 预期 |
|------|------|------|------|
| D1 | 检索 | 「先 search_available_tools：query【word 插入】」 | 返回含 `current_word_insert_text` 等裸名 |
| D2 | 激活 | `activate_tools` 激活上一问中的 **一个** Word 工具 | 成功后同轮后续 `tool_calls` 可命中该工具 |
| D3 | 与宿主一致 | 在 **表格** 宿主下检索「excel 读取」并激活 | 仅 Excel 类工具可激活；不应出现跨宿主误激活仍执行成功 |

---

## 十、Pdf 与通用插件（抽样）

| 序号 | 场景 | 说明 |
|------|------|------|
| P1 | `get_pdf_text` | 传入**服务端可读**的绝对路径；WPS 端无 `File.get_attachment_path` 时须用户口述或粘贴路径 |
| P2 | `CLI.run_command` | 若未禁用 `cli`：子任务或主会话白名单命令；与 Chrome 类似需关注 HITL |
| P3 | Memory | Embedding 已配置时抽测 `save_memory` / `search_memory` |

---

## 十一、记录模板（测试时可复制）

```
日期：
WPS 版本（文字/表格/演示）：
后端版本 / 分支：
wpsjs / 加载项构建方式：

| 用例编号 | 宿主（word/et/wpp） | 工具名 | 命中 Y/N | PASS/FAIL | 现象与备注 |
|----------|---------------------|--------|----------|-----------|------------|
| WD1 | word | current_word_read_body | | | |
| … | | | | | |
```

---

## 十二、维护说明

- **增删 RPC 分支**（`handleRpcRequest` 新方法或改 `getWpsHostKind`）时，同步本文 **§三～§六** 与 `docs/WPS插件调试指南.md` §3.1。  
- **增删 `DOCUMENT_SCRIPTS`** 时，同步 **§七** 与 `CurrentDocumentPlugin` 工具描述。  
- **改 `ClientTypeToolFilter`（含 WPS 收窄）** 时，同步 **§一 C3/C4** 与 `docs/应用内AI插件列表.md` §三。

*校准日期：2026-04-15（对齐 `wps-addin-new` 任务窗格 RPC 与后端 `CurrentDocument` / `ClientTypeToolFilter`）。*
