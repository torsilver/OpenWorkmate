# Semantic Kernel 尚未完全发挥的能力 — 分步计划

本文档记录当前后台（OfficeCopilot.Server）所用 **Microsoft Semantic Kernel 1.72.0** 中尚未完全发挥的能力，并给出分步实施计划，便于按优先级逐步落地，避免一次性大改。

---

## 一、当前已较好使用的 SK 能力（对照用）

- Chat Completion（多轮对话、系统提示、流式）
- 自动函数调用（Auto Function Invocation）
- 插件：Native（CLI/Excel/Word/Browser/File/Tavily/ClawhubSkill）+ 用户技能（Prompt 转 Function）+ MCP
- 过滤器：SecurityFilter、SessionContextFilter、ToolStatusFilter
- 多模型注册（OpenAI / Azure），按 serviceId 切换
- **两阶段工具选择（主模型）**：启用时，第一次主模型根据对话内容+工具分类信息选子类（不传 tools）；第二次仅将选中子类下的具体工具交给主模型一次对话并调用，以平衡工具量大时的负载。已完全移除嵌入式/本地小模型。
- **Embedding（仅远程 API）**：记忆与 RAG 的向量化仅支持远程 Embedding API；已移除本地 GGUF/LLamaSharp 能力。

---

## 二、尚未完全发挥的能力清单

| 序号 | 能力 | 说明 | 优先级建议 |
|------|------|------|------------|
| 1 | 嵌入 + RAG / 记忆 | 文本向量化、向量存储、检索增强生成或长期记忆 | 按需求 |
| 2 | 多模型连接器 | 配置已有 Ollama/Anthropic，仅实现了 OpenAI/Azure | 中 |
| 3 | ~~LLM 驱动工具选择~~ | **已实现**：主模型两阶段（先选分类，再仅带选中分类下的工具对话）；嵌入式工具选择已移除 | — |
| 4 | 提示模板与变量 | 用户技能为纯文本模板，未用 Handlebars/变量 | 低 |
| 5 | 图像生成 | 未接入 DALL·E 等文生图能力 | 按需求 |

---

## 三、分步实施计划

### 阶段 1：补齐已有配置（多模型连接器）

**目标**：让配置里的 Provider（如 Ollama、Anthropic）真正生效，而不是只支持 OpenAI/Azure。

**步骤**：

1. **调研与依赖**
   - 查 SK 官方文档 / NuGet：Ollama、Anthropic（或 OpenAI 兼容端点）对应的 C# 包与 API。
   - 在 `OfficeCopilot.Server.csproj` 中按需添加包（如 `Microsoft.SemanticKernel.Connectors.Ollama` 等）。

2. **扩展 RebuildKernelAsync**
   - 在 [backend/ChatService.cs](backend/ChatService.cs) 的 `RebuildKernelAsync` 中，对 `entry.Provider` 增加分支：
     - 已有：`OpenAI`、`Azure`（保持现状）。
     - 新增：`Ollama`、`Anthropic`（或其它）对应 `AddXxxChatCompletion(...)`，并传入 `serviceId: entry.Id`。

3. **配置契约**
   - 在 [backend/ConfigService.cs](backend/ConfigService.cs)（或相关 DTO）中确认 Ollama/Anthropic 所需字段（如 BaseUrl、ApiKey、ModelId）与前端/配置一致。

4. **验证**
   - 在设置页添加一条 Ollama（或 Anthropic）模型，保存后发一条对话，确认 Kernel 使用对应服务并正常回复。

**产出**：配置中的多 Provider 均可被注册并使用。

---

### 阶段 2：工具选择两阶段（主模型）— 已完成

**目标**：工具量大时避免全量 tools 导致本地模型拒载；两阶段均由主模型完成，不再使用嵌入式/本地小模型。

**当前实现**（[backend/Services/ToolSelectionService.cs](backend/Services/ToolSelectionService.cs)、[backend/ChatService.cs](backend/ChatService.cs)）：

- **阶段一**：主模型一次调用，输入为对话内容 + 工具分类信息（子类 id 与描述），**不传 tools**；输出为选中的子类 id 列表（或「全部」）。失败或「全部」时使用全量工具。
- **阶段二**：根据阶段一得到的子类，仅将对应子类下的具体工具交给主模型，一次流式对话完成回复与按需工具调用；不再单独用 LLM 从候选函数中再选一轮。
- **配置**：`ToolSelectionTwoStage == true`（默认）启用上述两阶段；为 false 时可按插件名由主模型筛选（`SelectPluginNamesAsync`）或全量。
- **已完全移除**：嵌入式/本地工具选择模型（EmbeddedToolSelectionModel、ToolSelectionModelId、EmbeddedToolSelectionModelPath）及对应设置页 UI。

---

### 阶段 3：嵌入与 RAG / 记忆（按需）

**目标**：支持“基于文档/知识库的问答”或“跨轮次记忆”，利用 SK 的嵌入与记忆能力。

**当前**：Embedding 仅支持远程 API（如 OpenAI Embeddings、Azure OpenAI Embeddings）；已移除本地 GGUF/LLamaSharp。

**步骤**：

1. **选型**
   - 嵌入服务：与当前主模型一致或独立的远程嵌入模型（如 OpenAI Embeddings、Azure OpenAI Embeddings）。
   - 存储：内存/文件/向量库（如 Qdrant、Milvus、或 SK 示例中的简单向量存储），按规模选择。

2. **依赖**
   - 添加 SK 嵌入相关包（通常主包已带或需 `Microsoft.SemanticKernel.Connectors.OpenAI` 的 Embedding 扩展），在 Kernel 中注册 `ITextEmbeddingGenerationService`。

3. **记忆/检索插件**
   - 封装“写入记忆/索引”和“按查询检索”为 Kernel 插件（或使用 SK 的 Memory 插件形态），供主模型在对话中调用；或在后端在每轮对话前自动检索相关记忆注入上下文。

4. **配置与数据**
   - 配置项：是否启用 RAG/记忆、嵌入模型、存储路径或连接串。
   - 若涉及敏感数据，需考虑权限与脱敏。

**产出**：可选启用“记忆/知识库”能力，模型能基于检索结果或长期记忆回答。

---

### 阶段 4：提示模板与变量（低优先级）

**目标**：用户技能支持变量（如 `{{$userName}}`、`{{$date}}`），或条件/循环，便于做个性化或时间敏感提示。

**步骤**：

1. **模板引擎**
   - 方案 A：引入 SK 的 Handlebars 等模板支持，将 `skill.PromptTemplate` 视为模板字符串，在创建 `CreateFunctionFromPrompt` 时传入 SK 的模板引擎。
   - 方案 B：在现有逻辑中做一层简单替换（如 `{{name}}` → 从 KernelArguments 或会话上下文取值），不引入 Handlebars。

2. **变量来源**
   - 定义变量来源：会话元数据、当前时间、用户设置等，并在调用用户技能时注入到 KernelArguments。

3. **兼容旧技能**
   - 无变量的现有技能保持原样可跑；新技能可选使用变量语法。

**产出**：用户技能支持简单或高级模板变量，便于扩展提示能力。

---

### 阶段 5：图像生成（按需）

**目标**：需要“根据描述生成图片”时，接入 DALL·E 等图像生成服务并暴露为 Kernel 插件。

**步骤**：

1. **依赖与服务**
   - 查 SK 文档/NuGet 中图像生成连接器（如 OpenAI 的 Image Generation），在 Kernel 或单独服务中注册。

2. **插件**
   - 新增一个 Plugin（如 `ImageGenPlugin`），提供 1～2 个 Kernel 函数（如 `generate_image(description, size)`），内部调用图像生成 API，返回图片 URL 或 base64。

3. **安全与策略**
   - 在 SecurityFilter 或配置中限制可调用的模型/尺寸/频率，避免滥用。

**产出**：模型在对话中可调用“生成图片”工具。

---

## 四、优先级与依赖关系

```text
阶段 1（多模型连接器）   → 可独立先做，无依赖
阶段 2（工具选择两阶段）  → 已完成，主模型两阶段选分类+带筛选工具
阶段 3（嵌入/RAG/记忆）  → 依赖阶段 1 的模型/配置扩展更佳，但可单独做
阶段 4（提示模板）       → 独立，低优先级
阶段 5（图像生成）       → 独立，按产品需求做
```

建议：先做 **阶段 1**，再按产品需求在 **阶段 2 / 3 / 5** 中选一项做，**阶段 4** 作为体验优化稍后做。

---

## 五、文档维护

- 每完成一个阶段，可在本文档对应阶段下增加“完成情况”和日期。
- 若 SK 升级或新增能力，可在此补充“尚未发挥”项并增补分步计划。
