using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using OfficeCopilot.Server.Mcp;

namespace OfficeCopilot.Server;

public class AiConfig
{
    public string Provider { get; set; } = "OpenAI";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "gpt-4o-mini";
    public string SystemPrompt { get; set; } = "你是 Office Copilot，一个智能办公自动化助手。你运行在用户的本地电脑上，能够帮助用户操作 Excel、Word 文档，执行系统命令。请用简洁友好的中文回答用户问题。\n如果用户让你画图、展示报表或动态页面，请直接返回一段完整的、带有 <html_canvas> 和 </html_canvas> 标签包裹的 HTML 代码（里面可以引入 Echarts 或其他 CDN 图表库），我会用浏览器渲染给用户看。\n用户可能从浏览器侧边栏、Word/Excel 任务窗格或 WPS 加载项连接：操作当前打开的文档请用 CurrentDocument（仅任务窗格/WPS 端可用），操作网页高亮与截图请用 Browser（仅浏览器端可用）；若当前端不支持会返回提示，可引导用户切换到对应端。\n当你获得大量结构化数据、表格或需要跨步骤精确引用的内容时，请使用 accurate_data_write 保存，之后用 accurate_data_read 按 id 取回，避免占用对话上下文。\n创建 Word 文档时，paragraphs 可用 | 显式分段，也可用空行或换行分段（服务端会拆成多个 Word 段落）；行首 Markdown：以 # / ## / ### 开头为标题，以 - 或 * 开头为列表项，其余为正文（自动首行缩进与排版）。创建 PPT 或向幻灯片写入正文时，bodyText/text 可用 | 显式分段，也可用空行或换行分段（与 Word 一致，服务端会拆成多行）；以 - 或 * 开头的行会自动变为项目符号。在当前文档中插入文字时，可用 style 参数指定样式（如 Heading1、Heading2）使文档结构清晰。\n本机文件与用户身份：工具运行在「当前登录 Windows 用户」的环境中，凡写入或引用用户个人目录、桌面、下载、文档等，均应对应该用户本人。不要臆造 C:\\Users\\某用户名\\…；不要用 C:\\Users\\Public、%PUBLIC% 等公共/共享配置目录代替当前用户的私人目录。需要绝对路径时优先用 %USERPROFILE% 及其子路径（如 Desktop、Downloads）。Word/Excel/PPT 等路径参数若未要求完整盘符路径，优先只传文件名或相对子路径（服务端按约定解析到当前用户下，多为 Downloads）。\n尽量在客户本机解决，减少token消耗的内容：优先通过本机工具完成提取/计算/转换，只把必要的摘要或最终结果回传到对话上下文，避免把原始大段数据（如超长文本或 base64）直接塞进 prompt。\n对可能大范围修改文件、执行系统命令或运行脚本的操作，应先向用户澄清影响范围与意图；系统可能对敏感操作要求人工确认，请配合。不要擅自扩大操作范围。\n工具接地：凡涉及本机 Excel/Word/PPT 或磁盘文件的实际变更（如合并/取消合并单元格、写入区域、保存文件、删除内容等），你必须先发出 function call 并由工具执行完成；仅在看到工具返回内容后，才能向用户确认「已成功」或说明失败原因。推理/思考过程不能代替工具调用，也不得仅凭意图描述冒充已执行。\n文件状态接地：对话里更早的助手摘要或较早轮次的工具输出**可能已过时**，不能当作磁盘上文件的当前真相（模型无法可靠区分「历史叙述」与「此刻文件」）。凡用户询问、核对或点名读取某 Word/Excel/PPT 路径下的实际内容（正文、表格、幻灯片、形状、列表等），你必须**当场再次调用**对应只读工具，并以**本轮工具返回**为唯一依据作答；禁止仅凭对话记忆复述成「已读过」或推断当前文件状态。若本轮尚未调用成功或未收到返回，应调用工具或如实说明，不得用历史内容凑答案。";
    /// <summary>始终包含的插件名（如 CLI），即使用户未提到也会传给模型（仅旧版 JSON 的 <c>ai</c> 对象内使用；运行时应使用 <see cref="AppConfig.AlwaysIncludePlugins"/>）。</summary>
    public List<string> AlwaysIncludePlugins { get; set; } = new();
}

/// <summary>与 <see cref="AiConfig"/> 默认构造一致的嵌入式主 system 文案，供无 <see cref="AiModelEntry.SystemPrompt"/> 时回退。</summary>
public static class AiEmbeddedDefaults
{
    public static readonly string DefaultSystemPrompt = new AiConfig().SystemPrompt;
}

/// <summary>多模型列表中的单条：支持 OpenAI / Azure / Ollama / Anthropic。</summary>
/// <remarks>显式 JsonPropertyName 确保前端 camelCase（如 apiKey）在嵌套反序列化时正确绑定，避免源生成器对嵌套类型未应用命名策略导致 ApiKey 丢失。</remarks>
public class AiModelEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";
    /// <summary>类型：OpenAI, Azure, Ollama, Anthropic。</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "OpenAI";
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";
    /// <summary>Azure 部署名；仅 Provider=Azure 时使用。</summary>
    [JsonPropertyName("deploymentName")]
    public string DeploymentName { get; set; } = "";
    /// <summary>Azure API 版本；仅 Provider=Azure 时使用。</summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = "2024-02-01";
    /// <summary>可选；不填则使用 AI.SystemPrompt 全局默认。</summary>
    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; set; } = "";
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
    /// <summary>单模型上下文 token 上限；存在时覆盖全局 MaxContextTokens（如内部 64K、云端 128K）。</summary>
    [JsonPropertyName("contextLength")]
    public int? ContextLength { get; set; }
    /// <summary>Chrome 设置的 AI 供应商 id，用于反显与默认 endpoint；与 provider 并存。</summary>
    [JsonPropertyName("vendorId")]
    public string VendorId { get; set; } = "";

    /// <summary>阿里云百炼 OpenAI 兼容：是否开启混合思考（请求体 <c>enable_thinking</c>）。<c>null</c> 表示不写入，使用模型默认。</summary>
    [JsonPropertyName("enableThinking")]
    public bool? EnableThinking { get; set; }

    /// <summary>百炼：<c>thinking_budget</c>，推理过程 token 上限；需与 <see cref="EnableThinking"/> 配合。</summary>
    [JsonPropertyName("thinkingBudget")]
    public int? ThinkingBudget { get; set; }

    /// <summary>百炼：<c>enable_search</c> 联网搜索。</summary>
    [JsonPropertyName("enableSearch")]
    public bool? EnableSearch { get; set; }

    /// <summary>百炼：<c>search_options</c> 的 JSON 对象字符串（与官方文档字段一致），可选。</summary>
    [JsonPropertyName("searchOptionsJson")]
    public string? SearchOptionsJson { get; set; }

    /// <summary>百炼流式：<c>stream_options.include_usage</c>。</summary>
    [JsonPropertyName("streamIncludeUsage")]
    public bool? StreamIncludeUsage { get; set; }

    /// <summary>摘要/工具筛选等后台调用是否强制关闭思考（写入 <c>enable_thinking: false</c>），减轻延迟与费用。</summary>
    [JsonPropertyName("disableThinkingForBackgroundCalls")]
    public bool DisableThinkingForBackgroundCalls { get; set; } = true;

    /// <summary>为 true 时，用户通过附件上传的图片在同轮对话中会以 <c>ImageContent</c> 注入聊天 API（需模型与端点支持视觉）。仍为 <c>attachment:</c> 引用，OCR 等工具可用。</summary>
    [JsonPropertyName("supportsVision")]
    public bool SupportsVision { get; set; }
}

/// <summary>Embedding 模型列表中的单条；仅支持 Remote 远程 API。</summary>
public class EmbeddingModelEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";
    [JsonPropertyName("source")]
    public string Source { get; set; } = "Remote";
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";
    [JsonPropertyName("vendorId")]
    public string VendorId { get; set; } = "";
}

/// <summary>会话配置：历史轮数、超时等。</summary>
public class SessionConfig
{
    public int MaxHistoryTurns { get; set; } = 80;
    public int MinTurnsToKeep { get; set; } = 8;
    public int TimeoutMinutes { get; set; } = 30;
    public int CleanupIntervalMinutes { get; set; } = 5;
}

/// <summary>上下文窗口配置：64K 优化及业内常用项，便于将来换硬件时改配置即可。</summary>
public class ContextWindowConfig
{
    public int MaxContextTokens { get; set; } = 64_000;
    public int ReservedSystemTokens { get; set; } = 12_000;
    public int ReservedToolsTokens { get; set; } = 12_000;
    public int ReservedOutputTokens { get; set; } = 4_096;
    public int PlanContentMaxChars { get; set; } = 16_000;
    public int MemoryInjectionMaxChars { get; set; } = 4_000;
    public int MemorySessionTopK { get; set; } = 5;
    public int MemorySharedTopK { get; set; } = 3;
    public string TokenEstimation { get; set; } = "CharsRatio";
    public int CharsPerToken { get; set; } = 2;
    public bool SummarizationEnabled { get; set; }
    public double SummarizationTriggerRatio { get; set; } = 0.9;
    public int SummarizationMaxSummaryChars { get; set; } = 500;
    public bool ContextLengthRetryEnabled { get; set; } = true;
    public int ContextLengthRetryMaxTurns { get; set; } = 10;
    /// <summary>摘要时被压缩的对话历史落盘目录；为空时使用与 PlansDirectory 同级的 ConversationHistory，若仍无法解析则使用 %LocalAppData%/OfficeCopilot/ConversationHistory。</summary>
    public string? ConversationHistoryDirectory { get; set; }
    /// <summary>旧消息中大内容截断：当历史 token 占比超过此比例时，对「保留窗口」之外的旧消息做内容截断。0 表示禁用。建议低于 SummarizationTriggerRatio（如 0.7）。</summary>
    public double TruncateToolArgsThresholdRatio { get; set; }
    /// <summary>大内容截断时保留的最近消息条数（不含 system），此范围内的消息不截断。</summary>
    public int TruncateToolArgsKeepMessages { get; set; } = 10;
    /// <summary>单条消息内容截断后的最大字符数，超出部分替换为「…(已截断)」。</summary>
    public int TruncateToolArgsMaxChars { get; set; } = 2000;

    /// <summary>为 true 时主会话工具阶段先经一轮 LLM 判断是否需要绑定工具；为 false 时跳过门控、始终走两阶段（默认开启）。</summary>
    public bool EnableToolNeedGate { get; set; } = true;

    /// <summary>工具需求门控：拼入模型前的用户侧提示最大字符数（含 [上一条] 等），超出截断。</summary>
    public int ToolNeedGateMaxPromptChars { get; set; } = 1500;

    /// <summary>完全不优化（完全依赖大模型）：为 true 时不按 token 裁历史、不摘要、不截断工具参数、不触发超长重试；仅保留轮数上限。为 false 时使用本配置内其余优化参数。</summary>
    public bool PassThroughContext { get; set; }

    /// <summary>为 true 时向 <see cref="SessionAuditDirectory"/> 追加 JSONL 会话审计（用户消息、工具起止、压缩等）；默认关闭。</summary>
    public bool SessionAuditEnabled { get; set; }

    /// <summary>JSONL 审计目录；空则与 <see cref="ConversationHistoryDirectory"/> 同级下 SessionAudit，再否则 %LocalAppData%/OfficeCopilot/SessionAudit。</summary>
    public string? SessionAuditDirectory { get; set; }
}

/// <summary>上下文优化预设：一组 ContextWindow + Session，用于切换「公司内部 64K」「Kimi K2.5」或自定义。</summary>
public class ContextOptimizationPreset
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ContextWindowConfig ContextWindow { get; set; } = new();
    public SessionConfig Session { get; set; } = new();
}

/// <summary>测试 AI 连接时前端传入的请求体。</summary>
public class TestAiRequest
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? ModelId { get; set; }
    public string? Provider { get; set; }
    public string? DeploymentName { get; set; }
    [JsonPropertyName("vendorId")]
    public string? VendorId { get; set; }
}

/// <summary>测试 Embedding 连接时前端传入的请求体。</summary>
public class TestEmbeddingRequest
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? ModelId { get; set; }
    [JsonPropertyName("vendorId")]
    public string? VendorId { get; set; }
}

/// <summary>OCR 模型列表中的单条。</summary>
public class OcrModelEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
    [JsonPropertyName("language")]
    public string? Language { get; set; }
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";
    [JsonPropertyName("connectionKind")]
    public string ConnectionKind { get; set; } = "";
    [JsonPropertyName("vendorId")]
    public string VendorId { get; set; } = "";
}

/// <summary>测试百炼实时语音识别（v1/inference WebSocket）。</summary>
public class TestRealtimeAsrRequest
{
    public string? ApiKey { get; set; }
    public string? WebSocketBaseUrl { get; set; }
    public string? ModelId { get; set; }
}

/// <summary>测试 OCR 连接时前端传入的请求体。</summary>
public class TestOcrRequest
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Language { get; set; }
    public string? ModelId { get; set; }
    public string? ConnectionKind { get; set; }
    public string? VendorId { get; set; }
}

/// <summary>阿里云百炼实时语音识别（WebSocket <c>/api-ws/v1/inference</c>）；用于侧栏语音输入、会议流式识别及工具转写。</summary>
public class RealtimeAsrConfig
{
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    /// <summary>北京默认 <c>wss://dashscope.aliyuncs.com/api-ws/v1/inference</c>；国际为 <c>wss://dashscope-intl.aliyuncs.com/api-ws/v1/inference</c>。</summary>
    [JsonPropertyName("webSocketBaseUrl")]
    public string WebSocketBaseUrl { get; set; } = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "fun-asr-realtime";

    /// <summary>仅 Paraformer 等模型生效，如 zh、en。</summary>
    [JsonPropertyName("languageHints")]
    public List<string>? LanguageHints { get; set; }

    /// <summary>长连接静音保活（文档建议会议场景开启）。</summary>
    [JsonPropertyName("heartbeat")]
    public bool Heartbeat { get; set; } = true;

    /// <summary>语义断句（Paraformer v2）；会议监听由服务端按 mode 覆盖。</summary>
    [JsonPropertyName("semanticPunctuationEnabled")]
    public bool SemanticPunctuationEnabled { get; set; } = true;

    [JsonPropertyName("disfluencyRemovalEnabled")]
    public bool DisfluencyRemovalEnabled { get; set; }

    /// <summary>可选百炼业务空间 ID，对应请求头 X-DashScope-WorkSpace。</summary>
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }
}

/// <summary>OCR 配置，用于内置 MCP_OCR 工具；调用远程 OCR API（如 Azure Document Intelligence 或兼容接口）。</summary>
public class OcrConfig
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>可选语言或模型标识，视具体 API 而定。</summary>
    public string? Language { get; set; }
}

/// <summary>MAF 对话编排实验开关（user-config.json 键名仍为历史名 <c>semanticKernel</c>）。</summary>
public class SemanticKernelFeaturesConfig
{
    /// <summary>主模型流式前增加轻量 <c>ChatClientAgent</c>（Host 前言），输出走 agent_trace；可临时影响本轮 <c>historyToUse</c>。</summary>
    public bool UseHostPreambleAgent { get; set; }
    /// <summary>主会话使用 MAF <c>AgentGroupChat</c>（Host + Worker）；延迟与 token 消耗显著增加，实验用。</summary>
    public bool UseAgentGroupChatMainSession { get; set; }

    public static SemanticKernelFeaturesConfig Clone(SemanticKernelFeaturesConfig? src)
    {
        if (src == null) return new SemanticKernelFeaturesConfig();
        return new SemanticKernelFeaturesConfig
        {
            UseHostPreambleAgent = src.UseHostPreambleAgent,
            UseAgentGroupChatMainSession = src.UseAgentGroupChatMainSession
        };
    }
}

public class AppConfig
{
    /// <summary>始终包含的插件名（如 CLI）；与 <c>aiModels</c> 并列存于 user-config.json 顶层。</summary>
    [JsonPropertyName("alwaysIncludePlugins")]
    public List<string> AlwaysIncludePlugins { get; set; } = new();

    /// <summary>旧版单条 <c>ai</c>；读入后由 <see cref="MigrateLegacyAiIfNeeded"/> 合并到 <see cref="AiModels"/> / <see cref="AlwaysIncludePlugins"/>，保存时省略。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiConfig? AI { get; set; }
    /// <summary>MAF 编排实验开关（JSON 键 <c>semanticKernel</c> 为历史名）；未配置时均为 false。</summary>
    public SemanticKernelFeaturesConfig? SemanticKernel { get; set; }
    /// <summary>会话配置（历史轮数、超时等）；未配置时使用默认值。</summary>
    public SessionConfig? Session { get; set; }
    /// <summary>上下文窗口配置（64K 优化、预留、摘要、重试等）；未配置时使用默认值。</summary>
    public ContextWindowConfig? ContextWindow { get; set; }
    /// <summary>上下文优化预设列表（内置 64K/Kimi K2.5 + 用户自定义）；空时在加载时注入内置两条。</summary>
    public List<ContextOptimizationPreset>? ContextOptimizationPresets { get; set; }
    /// <summary>当前生效的预设 Id；非空且存在于 Presets 时，加载后用该预设覆盖 Session/ContextWindow。</summary>
    public string? ActiveContextPresetId { get; set; }
    /// <summary>多套 AI 模型列表；运行时的唯一来源（旧 <c>ai</c> 仅在加载时迁移进此列表）。</summary>
    public List<AiModelEntry> AiModels { get; set; } = new();
    /// <summary>当前使用的模型 Id，对应 AiModels 中某条的 Id。</summary>
    public string ActiveModelId { get; set; } = "";
    public List<McpServerConfig> McpServers { get; set; } = new();
    /// <summary>全局 CLI/页面脚本运行模式：RunEverything | AskEverytime | UseAllowList；全端共用。<see cref="ConfigService.GetCliRunModeForEnd"/> 对 <c>backend</c> 会将 AskEverytime 视为 UseAllowList（无 HITL）。</summary>
    public string CliRunMode { get; set; } = "UseAllowList";
    /// <summary>
    /// 按端命令白名单（每项为命令名，如 dir、echo、type）；空则使用默认列表。
    /// 键为 <see cref="CliScriptEndKeys"/> 中的四端：chrome、backend、office、wps（彼此独立）。
    /// <see cref="CliScriptEndKeys.Office"/> 对应所有 clientType 以 <c>office-</c> 开头的会话（Word/Excel/PowerPoint 共用同一套）。
    /// </summary>
    public Dictionary<string, List<string>> AllowedCliCommandsByClient { get; set; } = new();
    /// <summary>
    /// 按端 <c>run_page_script</c> 的 scriptId 白名单；空则使用默认列表。
    /// 键同上。仅在会话会调用 Browser 插件时参与校验（主要为 <c>chrome</c>；Office/WPS 不向模型暴露 <c>run_page_script</c>，对应键多为保留项）。
    /// </summary>
    public Dictionary<string, List<string>> AllowedPageScriptIdsByClient { get; set; } = new();
    /// <summary>
    /// 按端 <c>current_run_document_script</c> 的预定义 scriptId 白名单；空则使用 <see cref="CliScriptEndKeys"/> 中与任务窗格注册表一致的默认列表。
    /// 键主要为 <c>office</c>、<c>wps</c>（与宿主侧 <c>DOCUMENT_SCRIPTS</c> 对齐）。
    /// </summary>
    public Dictionary<string, List<string>> AllowedDocumentScriptIdsByClient { get; set; } = new();
    /// <summary>已停用的内置插件 ID 列表（如 Browser、File、CLI、Excel、Word），这些插件不会注册到 Kernel。</summary>
    public List<string> DisabledBuiltInPlugins { get; set; } = new();
    /// <summary>技能所需环境变量统一配置：键为环境变量名（如 OPENAI_API_KEY），值为配置内容。执行 Clawhub 脚本时优先从此处读取。可在设置页或 user-config.json 中配置。</summary>
    public Dictionary<string, string> SkillEnv { get; set; } = new();
    // ----- 阶段 3：嵌入与 RAG / 记忆 -----
    /// <summary>Embedding 模型列表；支持多条，仅 Remote 远程 API。</summary>
    public List<EmbeddingModelEntry>? EmbeddingModels { get; set; }
    /// <summary>当前使用的 Embedding 模型 Id，对应 EmbeddingModels 中某条的 Id；为空或不在列表中则视为未配置。</summary>
    public string? ActiveEmbeddingModelId { get; set; }
    /// <summary>向量存储类型：Memory = 内存，Sqlite = 本地 db 文件。</summary>
    public string RagStorageType { get; set; } = "Sqlite";
    /// <summary>SQLite 向量库路径（RagStorageType=Sqlite 时）；为空时使用 %LocalAppData%/OfficeCopilot/rag.db。</summary>
    public string? RagStoragePath { get; set; }
    /// <summary>计划存储目录（.plan.md 文件）；为空时使用 %LocalAppData%/OfficeCopilot/Plans。</summary>
    public string? PlansDirectory { get; set; }
    /// <summary>准确数据插件存储目录；为空时使用 %LocalAppData%/OfficeCopilot/AccurateData。</summary>
    public string? AccurateDataDirectory { get; set; }
    /// <summary>定时任务插件存储目录（.task.md 文件）；为空时使用 %LocalAppData%/OfficeCopilot/ScheduledTasks。</summary>
    public string? ScheduledTasksDirectory { get; set; }
    /// <summary>OCR 配置；为空时 MCP_OCR 工具不可用。已废弃，请使用 OcrModels + ActiveOcrModelId。</summary>
    public OcrConfig? Ocr { get; set; }
    /// <summary>百炼实时语音识别（v1/inference WebSocket）；语音输入、会议与文件转写均依赖此项。</summary>
    public RealtimeAsrConfig? RealtimeAsr { get; set; }
    /// <summary>OCR 模型列表；支持多条。</summary>
    public List<OcrModelEntry> OcrModels { get; set; } = new();
    /// <summary>当前使用的 OCR 模型 Id，对应 OcrModels 中某条的 Id。</summary>
    public string? ActiveOcrModelId { get; set; }
    /// <summary>对话界面预设主题：light | dark | blocks | modern | minimal | lines | sketch；空或未识别时前端按 dark 处理。</summary>
    public string? UiThemeId { get; set; }
    /// <summary>Chrome 扩展 ID（chrome://extensions 中「ID」列），托盘「设置」用于打开 chrome-extension://…/options.html。请在 user-config.json 中填写。</summary>
    public string? ChromeExtensionId { get; set; }
    /// <summary>为 true 时，设置页「测试连接」可向 localhost、RFC1918 等地址发请求（默认 false，降低 SSRF 风险）。</summary>
    public bool AllowPrivateEndpointTests { get; set; }
    /// <summary>本地 HTTP/WebSocket 访问密钥；在扩展选项页保存后写入 user-config.json，各端可从本机引导接口自动同步。</summary>
    public string? WebSocketAuthToken { get; set; }

    /// <summary>按 <c>Plugin:function</c> 通配符（<c>*</c>）匹配的工具权限覆盖；多条命中时按 Deny &gt; Ask &gt; AllowAlways &gt; AllowOnceSession。</summary>
    public List<ToolPermissionRule>? ToolPermissionRules { get; set; }
}

/// <summary>单条工具权限规则；<see cref="Pattern"/> 形如 <c>CLI:*</c>、<c>Excel:excel_*</c>、<c>*:run_command</c>。</summary>
public sealed class ToolPermissionRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    /// <summary>deny | ask | allowAlways | allowOnceSession（大小写不敏感）。</summary>
    [JsonPropertyName("effect")]
    public string Effect { get; set; } = "";
}

/// <summary>CLI/页面脚本安全策略用的四端键名：chrome、backend、office、wps；配置中按端各有一份白名单。</summary>
public static class CliScriptEndKeys
{
    public const string Chrome = "chrome";
    public const string Backend = "backend";
    /// <summary>对应 <c>office-word</c>、<c>office-excel</c>、<c>office-powerpoint</c>（共用此键）。若需按应用拆分白名单，需扩展键名并同步 <see cref="ResolveEndKey"/> 与设置 UI。</summary>
    public const string Office = "office";
    public const string Wps = "wps";

    public static readonly string[] DefaultAllowedCommands = { "dir", "echo", "type", "ping", "systeminfo", "ipconfig" };

    /// <summary>Chrome <c>run_page_script</c> 默认白名单（与 <c>chrome-extension/options.js</c> <c>DEFAULT_PAGE_SCRIPTS</c> 一致）。<c>tab_open</c> 可导航至任意 URL，默认不包含，由用户在设置中手动加入。</summary>
    public static readonly string[] DefaultAllowedScriptIds =
    {
        "get_visible_text", "get_page_title", "get_page_outline", "extract_links", "extract_tables",
        "scroll_to_top", "scroll_to_bottom", "scroll_by", "scroll_into_view",
        "wait_for_selector",
        "click_selector", "fill_input", "select_option", "set_checked", "hover_selector", "focus_selector", "press_key",
        "tab_list", "tab_activate", "tab_reload", "tab_go_back", "tab_go_forward", "tab_close"
    };

    /// <summary>Office 任务窗格 <c>DOCUMENT_SCRIPTS</c> 预置 scriptId（与 <c>office-addin/taskpane.js</c> 对齐）。</summary>
    public static readonly string[] DefaultAllowedDocumentScriptIdsOffice =
    {
        "word_read_selection", "office_doc_meta", "office_word_body_preview", "office_host_quick_glance"
    };

    /// <summary>WPS 任务窗格 <c>DOCUMENT_SCRIPTS</c> 预置 scriptId（与 <c>wps-addin-new</c> 对齐）。</summary>
    public static readonly string[] DefaultAllowedDocumentScriptIdsWps =
    {
        "word_read_selection", "wps_doc_meta", "wps_word_body_preview", "wps_ppt_slide_glance"
    };

    /// <summary>
    /// 某端未配置 <see cref="AppConfig.AllowedCliCommandsByClient"/> 时使用的默认 CMD 名列表（与设置页「后台」内置勾选项一致）。
    /// 非 <c>backend</c> 端与后台共用同一套默认，便于设置页仅在「后台」维护 CLI；运行时仍按会话来源键解析白名单（空则回退到后台已配置列表，见 <see cref="ConfigService.GetAllowedCliCommandsForEnd"/>）。
    /// </summary>
    public static IReadOnlyList<string> GetDefaultAllowedCliCommands(string? endKey)
    {
        if (string.IsNullOrWhiteSpace(endKey)) return DefaultAllowedCommands;
        if (string.Equals(endKey, Backend, StringComparison.OrdinalIgnoreCase)) return DefaultAllowedCommands;
        return DefaultAllowedCommands;
    }

    /// <summary>某端未配置 <see cref="AppConfig.AllowedDocumentScriptIdsByClient"/> 时使用的默认预定义文档 scriptId。</summary>
    public static IReadOnlyList<string> GetDefaultAllowedDocumentScriptIds(string? endKey)
    {
        if (string.Equals(endKey, Office, StringComparison.OrdinalIgnoreCase)) return DefaultAllowedDocumentScriptIdsOffice;
        if (string.Equals(endKey, Wps, StringComparison.OrdinalIgnoreCase)) return DefaultAllowedDocumentScriptIdsWps;
        return Array.Empty<string>();
    }

    /// <summary>将 WebSocket 的 clientType 解析为四端键之一，用于查找 <see cref="AppConfig.AllowedCliCommandsByClient"/> 等。</summary>
    public static string ResolveEndKey(string? clientType)
    {
        var ct = (clientType ?? "").Trim();
        if (string.IsNullOrEmpty(ct)) return Backend;
        if (string.Equals(ct, "chrome", StringComparison.OrdinalIgnoreCase)) return Chrome;
        if (ct.StartsWith("office-", StringComparison.OrdinalIgnoreCase)) return Office;
        if (string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase)) return Wps;
        return Backend;
    }
}

public sealed class ConfigService
{
    /// <summary>与前端 camelCase 一致，用于从文件或 POST body 反序列化 AppConfig（含嵌套 embeddingModels[].endpoint）。</summary>
    public static readonly JsonSerializerOptions AppConfigDeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _configPath;
    private AppConfig _currentConfig;
    private readonly ILogger<ConfigService> _logger;
    private readonly object _lock = new();

    public event Action? OnConfigChanged;

    /// <param name="hostConfiguration">仅用于解析 <c>OfficeCopilot:UserConfigPath</c>（测试指向临时文件）；应用配置内容一律来自该路径下的 JSON。</param>
    public ConfigService(IConfiguration hostConfiguration, ILogger<ConfigService> logger)
    {
        _logger = logger;
        // 使用用户本地应用数据目录，与运行目录无关，避免 dotnet run/clean 后配置丢失
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData) || !Path.IsPathRooted(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? AppContext.BaseDirectory;
        var appDir = Path.Combine(appData, "OfficeCopilot");
        try { Directory.CreateDirectory(appDir); } catch { /* 无权限时后续写文件会报错 */ }

        // 允许测试/开发环境覆盖配置落盘路径，避免互相污染本机配置。
        // Key: OfficeCopilot:UserConfigPath
        var configPath = Path.Combine(appDir, "user-config.json");
        var overridePath = hostConfiguration["OfficeCopilot:UserConfigPath"];
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            overridePath = Environment.ExpandEnvironmentVariables(overridePath.Trim());
            if (Path.IsPathRooted(overridePath))
                configPath = overridePath;
            else
                configPath = Path.Combine(appDir, overridePath);

            var parent = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(parent))
                try { Directory.CreateDirectory(parent); } catch { /* ignored */ }
        }

        _configPath = configPath;
        _logger.LogInformation("Config file path: {Path} (exists: {Exists})", _configPath, File.Exists(_configPath));
        if (!File.Exists(_configPath))
        {
            try
            {
                WriteInitialDefaultUserConfigFile();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write default user-config.json to {Path}", _configPath);
            }
        }

        _currentConfig = LoadConfigFromDisk();
    }

    public AppConfig Current => _currentConfig;

    /// <summary>供 WebSocket 与 HTTP API 鉴权：仅使用 <see cref="AppConfig.WebSocketAuthToken"/>（user-config.json）。</summary>
    public string GetEffectiveWebSocketAuthToken()
    {
        lock (_lock)
        {
            return (_currentConfig.WebSocketAuthToken ?? "").Trim();
        }
    }

    /// <summary>获取指定端的 CLI/脚本运行模式（全局 <see cref="AppConfig.CliRunMode"/>）。后台端无 HITL 时 AskEverytime 按 UseAllowList 处理。</summary>
    public string GetCliRunModeForEnd(string endKey)
    {
        var mode = (_currentConfig.CliRunMode ?? "").Trim();
        if (string.IsNullOrEmpty(mode)) mode = "UseAllowList";
        if (string.Equals(endKey, CliScriptEndKeys.Backend, StringComparison.OrdinalIgnoreCase)
            && string.Equals(mode, "AskEverytime", StringComparison.OrdinalIgnoreCase))
            return "UseAllowList";
        return mode;
    }

    /// <summary>获取指定端的命令白名单；空或未配置时返回 null（调用方使用默认列表）。非 <c>backend</c> 键若未单独配置，则回退为 <c>backend</c> 键的列表（与设置页仅在「后台」维护 CLI 一致）。</summary>
    public IReadOnlyList<string>? GetAllowedCliCommandsForEnd(string endKey)
    {
        if (_currentConfig.AllowedCliCommandsByClient == null) return null;
        if (_currentConfig.AllowedCliCommandsByClient.TryGetValue(endKey, out var list) && list != null && list.Count > 0)
            return list;
        if (!string.Equals(endKey, CliScriptEndKeys.Backend, StringComparison.OrdinalIgnoreCase))
            return GetAllowedCliCommandsForEnd(CliScriptEndKeys.Backend);
        return null;
    }

    /// <summary>获取指定端的页面脚本白名单；空或未配置时返回 null（调用方使用默认列表）。</summary>
    public IReadOnlyList<string>? GetAllowedPageScriptIdsForEnd(string endKey)
    {
        if (_currentConfig.AllowedPageScriptIdsByClient == null) return null;
        if (!_currentConfig.AllowedPageScriptIdsByClient.TryGetValue(endKey, out var list) || list == null || list.Count == 0)
            return null;
        return list;
    }

    /// <summary>获取指定端 <c>current_run_document_script</c> 的 scriptId 白名单；空或未配置时返回 null（调用方使用 <see cref="CliScriptEndKeys.GetDefaultAllowedDocumentScriptIds"/>）。</summary>
    public IReadOnlyList<string>? GetAllowedDocumentScriptIdsForEnd(string endKey)
    {
        if (_currentConfig.AllowedDocumentScriptIdsByClient == null) return null;
        if (!_currentConfig.AllowedDocumentScriptIdsByClient.TryGetValue(endKey, out var list) || list == null || list.Count == 0)
            return null;
        return list;
    }

    /// <summary>将命令名加入指定端白名单并持久化。</summary>
    public void AddAllowedCliCommandForEnd(string endKey, string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName)) return;
        var key = commandName.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _currentConfig.AllowedCliCommandsByClient ??= new Dictionary<string, List<string>>();
            if (!_currentConfig.AllowedCliCommandsByClient.TryGetValue(endKey, out var list) || list == null)
                _currentConfig.AllowedCliCommandsByClient[endKey] = list = new List<string>();
            if (list.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(key);
            SaveConfig(_currentConfig);
        }
    }

    /// <summary>将 scriptId 加入指定端白名单并持久化。</summary>
    public void AddAllowedPageScriptIdForEnd(string endKey, string scriptId)
    {
        if (string.IsNullOrWhiteSpace(scriptId)) return;
        var key = scriptId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _currentConfig.AllowedPageScriptIdsByClient ??= new Dictionary<string, List<string>>();
            if (!_currentConfig.AllowedPageScriptIdsByClient.TryGetValue(endKey, out var list) || list == null)
                _currentConfig.AllowedPageScriptIdsByClient[endKey] = list = new List<string>();
            if (list.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(key);
            SaveConfig(_currentConfig);
        }
    }

    /// <summary>获取当前选中的 Embedding 配置条目；未配置或未选中时返回 null。</summary>
    public EmbeddingModelEntry? GetActiveEmbeddingEntry()
    {
        var id = (_currentConfig.ActiveEmbeddingModelId ?? "").Trim();
        if (string.IsNullOrEmpty(id) || _currentConfig.EmbeddingModels == null) return null;
        return _currentConfig.EmbeddingModels.FirstOrDefault(e => string.Equals((e.Id ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>获取当前选中的 OCR 配置条目；未配置或未选中时若存在旧版 Ocr 则返回其等效条目，否则返回 null。</summary>
    public OcrModelEntry? GetActiveOcrEntry()
    {
        if (_currentConfig.OcrModels != null && _currentConfig.OcrModels.Count > 0)
        {
            var id = (_currentConfig.ActiveOcrModelId ?? "").Trim();
            if (!string.IsNullOrEmpty(id))
            {
                var entry = _currentConfig.OcrModels.FirstOrDefault(e => string.Equals((e.Id ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase));
                if (entry != null) return entry;
            }
            return _currentConfig.OcrModels[0];
        }
        var legacy = _currentConfig.Ocr;
        if (legacy != null && !string.IsNullOrWhiteSpace(legacy.Endpoint) && !string.IsNullOrWhiteSpace(legacy.ApiKey))
            return new OcrModelEntry { Id = "legacy", DisplayName = "OCR", Endpoint = legacy.Endpoint, ApiKey = legacy.ApiKey, Language = legacy.Language };
        return null;
    }

    private AppConfig LoadConfigFromDisk()
    {
        if (!File.Exists(_configPath))
        {
            _logger.LogWarning("User config file not found at {Path}, using in-memory defaults.", _configPath);
            return CreateDefaultAppConfig();
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigDeserializeOptions);
            if (config == null)
            {
                _logger.LogWarning("User config at {Path} was empty or invalid, using defaults.", _configPath);
                return CreateDefaultAppConfig();
            }

            ApplyLoadedUserConfigPostProcessing(json, config);
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load user config from {Path}, using defaults.", _configPath);
            return CreateDefaultAppConfig();
        }
    }

    private void ApplyLoadedUserConfigPostProcessing(string json, AppConfig config)
    {
        PatchEmbeddingEndpointsFromRawJson(json, config);
        _logger.LogInformation("Loaded user config from {Path}", _configPath);
        config.SkillEnv ??= new Dictionary<string, string>();
        config.AlwaysIncludePlugins ??= new List<string>();
        MigrateCliRunModeFromLegacyJson(json, config);
        config.AllowedCliCommandsByClient ??= new Dictionary<string, List<string>>();
        config.AllowedPageScriptIdsByClient ??= new Dictionary<string, List<string>>();
        config.AllowedDocumentScriptIdsByClient ??= new Dictionary<string, List<string>>();
        config.AiModels ??= new List<AiModelEntry>();
        config.EmbeddingModels ??= new List<EmbeddingModelEntry>();
        config.OcrModels ??= new List<OcrModelEntry>();
        config.Session ??= new SessionConfig();
        config.ContextWindow ??= new ContextWindowConfig();
        // 旧配置文件无 enableToolNeedGate 字段时 Json 会得到 false；产品默认应开启门控
        if (!json.Contains("\"enableToolNeedGate\"", StringComparison.OrdinalIgnoreCase))
            config.ContextWindow.EnableToolNeedGate = true;
        if (config.ContextOptimizationPresets == null || config.ContextOptimizationPresets.Count == 0)
            config.ContextOptimizationPresets = new List<ContextOptimizationPreset>(GetBuiltInPresets());
        else
        {
            var hasQwen35 = config.ContextOptimizationPresets.Exists(p =>
                string.Equals((p.Id ?? "").Trim(), "qwen35-plus", StringComparison.OrdinalIgnoreCase));
            if (!hasQwen35)
            {
                var add = GetBuiltInPresets().Find(p => string.Equals(p.Id, "qwen35-plus", StringComparison.OrdinalIgnoreCase));
                if (add != null) config.ContextOptimizationPresets.Add(add);
            }
        }
        ApplyActivePresetIfSet(config);
        MigrateLegacyAiIfNeeded(config);
        MigrateLegacyOcrIfNeeded(config);
    }

    private AppConfig CreateDefaultAppConfig()
    {
        var appConfig = new AppConfig
        {
            Session = new SessionConfig(),
            ContextWindow = new ContextWindowConfig(),
            ContextOptimizationPresets = new List<ContextOptimizationPreset>(GetBuiltInPresets()),
            ActiveContextPresetId = "internal-64k",
            SkillEnv = new Dictionary<string, string>(),
            AlwaysIncludePlugins = new List<string>(),
            AI = new AiConfig(),
            AiModels = new List<AiModelEntry>(),
            EmbeddingModels = new List<EmbeddingModelEntry>(),
            OcrModels = new List<OcrModelEntry>(),
            CliRunMode = "UseAllowList",
            RagStorageType = "Sqlite",
            SemanticKernel = new SemanticKernelFeaturesConfig(),
        };
        ApplyActivePresetIfSet(appConfig);
        MigrateLegacyAiIfNeeded(appConfig);
        MigrateLegacyOcrIfNeeded(appConfig);
        return appConfig;
    }

    private void WriteInitialDefaultUserConfigFile()
    {
        var cfg = CreateDefaultAppConfig();
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = JsonCtx.Default
        };
        var json = JsonSerializer.Serialize(cfg, typeof(AppConfig), options);
        File.WriteAllText(_configPath, json);
        _logger.LogInformation("Created default user-config at {Path}", _configPath);
    }

    /// <summary>从旧版 <c>cliRunModeByClient</c> 迁移到 <see cref="AppConfig.CliRunMode"/>（当 JSON 中尚无 <c>cliRunMode</c> 键时）。</summary>
    private static void MigrateCliRunModeFromLegacyJson(string json, AppConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("cliRunMode", out var cmEl) && cmEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(cmEl.GetString()))
                return;
            if (root.TryGetProperty("cliRunModeByClient", out var by) && by.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "chrome", "backend", "office", "wps" })
                {
                    if (by.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s))
                        {
                            config.CliRunMode = s;
                            return;
                        }
                    }
                }
                foreach (var p in by.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = p.Value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s))
                        {
                            config.CliRunMode = s;
                            return;
                        }
                    }
                }
            }
        }
        catch
        {
            /* ignored */
        }
    }

    /// <summary>从原始 JSON 补全 embeddingModels 中缺失的 Endpoint，避免反序列化未正确映射 endpoint 时丢失。</summary>
    private static void PatchEmbeddingEndpointsFromRawJson(string json, AppConfig config)
    {
        if (config.EmbeddingModels == null || config.EmbeddingModels.Count == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            // 兼容 camelCase 与 PascalCase（文件可能来自不同保存路径）
            if (!doc.RootElement.TryGetProperty("embeddingModels", out var arr) && !doc.RootElement.TryGetProperty("EmbeddingModels", out arr))
                return;
            if (arr.ValueKind != JsonValueKind.Array) return;
            var list = config.EmbeddingModels;
            for (var i = 0; i < list.Count && i < arr.GetArrayLength(); i++)
            {
                var entry = list[i];
                if (!string.IsNullOrWhiteSpace(entry.Endpoint)) continue;
                var el = arr[i];
                if (!el.TryGetProperty("endpoint", out var ep)) el.TryGetProperty("Endpoint", out ep);
                if (ep.ValueKind == JsonValueKind.String && ep.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    entry.Endpoint = s;
            }
        }
        catch { /* 补全失败则保持原样 */ }
    }

    /// <summary>保存时若新数据中某条 Embedding 的 Endpoint 为空，则从当前配置同 Id 条目保留，避免被覆盖丢失。</summary>
    private static void PreserveEmbeddingEndpointsFromCurrent(List<EmbeddingModelEntry> newList, List<EmbeddingModelEntry>? currentList)
    {
        if (currentList == null || currentList.Count == 0) return;
        foreach (var entry in newList)
        {
            if (!string.IsNullOrWhiteSpace(entry.Endpoint)) continue;
            var id = (entry.Id ?? "").Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var current = currentList.FirstOrDefault(e => string.Equals((e.Id ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase));
            if (current != null && !string.IsNullOrWhiteSpace(current.Endpoint))
                entry.Endpoint = current.Endpoint;
        }
    }

    /// <summary>内置预设：公司内部 64K、Kimi K2.5（256K）、通义 Qwen3.5-Plus（百炼 1M 思考模式规格）。</summary>
    private static List<ContextOptimizationPreset> GetBuiltInPresets()
    {
        return new List<ContextOptimizationPreset>
        {
            new ContextOptimizationPreset
            {
                Id = "internal-64k",
                DisplayName = "公司内部 64K",
                ContextWindow = new ContextWindowConfig
                {
                    MaxContextTokens = 64_000,
                    ReservedSystemTokens = 12_000,
                    ReservedToolsTokens = 12_000,
                    ReservedOutputTokens = 4_096,
                    PlanContentMaxChars = 16_000,
                    MemoryInjectionMaxChars = 4_000,
                    MemorySessionTopK = 5,
                    MemorySharedTopK = 3,
                    TokenEstimation = "CharsRatio",
                    CharsPerToken = 2,
                    SummarizationEnabled = false,
                    SummarizationTriggerRatio = 0.9,
                    SummarizationMaxSummaryChars = 500,
                    ContextLengthRetryEnabled = true,
                    ContextLengthRetryMaxTurns = 10,
                    ConversationHistoryDirectory = null,
                    TruncateToolArgsThresholdRatio = 0,
                    TruncateToolArgsKeepMessages = 10,
                    TruncateToolArgsMaxChars = 2000
                },
                Session = new SessionConfig { MaxHistoryTurns = 80, MinTurnsToKeep = 8, TimeoutMinutes = 30, CleanupIntervalMinutes = 5 }
            },
            new ContextOptimizationPreset
            {
                Id = "kimi-k25",
                DisplayName = "Kimi K2.5",
                ContextWindow = new ContextWindowConfig
                {
                    MaxContextTokens = 256_000,
                    ReservedSystemTokens = 16_000,
                    ReservedToolsTokens = 16_000,
                    ReservedOutputTokens = 8_192,
                    PlanContentMaxChars = 32_000,
                    MemoryInjectionMaxChars = 8_000,
                    MemorySessionTopK = 8,
                    MemorySharedTopK = 5,
                    TokenEstimation = "CharsRatio",
                    CharsPerToken = 2,
                    SummarizationEnabled = false,
                    SummarizationTriggerRatio = 0.9,
                    SummarizationMaxSummaryChars = 500,
                    ContextLengthRetryEnabled = true,
                    ContextLengthRetryMaxTurns = 15,
                    ConversationHistoryDirectory = null,
                    TruncateToolArgsThresholdRatio = 0,
                    TruncateToolArgsKeepMessages = 10,
                    TruncateToolArgsMaxChars = 2000
                },
                Session = new SessionConfig { MaxHistoryTurns = 150, MinTurnsToKeep = 12, TimeoutMinutes = 30, CleanupIntervalMinutes = 5 }
            },
            new ContextOptimizationPreset
            {
                Id = "qwen35-plus",
                DisplayName = "通义 Qwen3.5-Plus（百炼）",
                ContextWindow = new ContextWindowConfig
                {
                    // 与阿里云百炼模型表一致（思考模式）：上下文 1M、最大输入 983616、思维链上限 81920、最大输出 65536。
                    MaxContextTokens = 1_000_000,
                    ReservedSystemTokens = 20_000,
                    ReservedToolsTokens = 24_000,
                    ReservedOutputTokens = 100_000,
                    PlanContentMaxChars = 48_000,
                    MemoryInjectionMaxChars = 12_000,
                    MemorySessionTopK = 10,
                    MemorySharedTopK = 6,
                    TokenEstimation = "CharsRatio",
                    CharsPerToken = 2,
                    SummarizationEnabled = false,
                    SummarizationTriggerRatio = 0.9,
                    SummarizationMaxSummaryChars = 500,
                    ContextLengthRetryEnabled = true,
                    ContextLengthRetryMaxTurns = 20,
                    ConversationHistoryDirectory = null,
                    TruncateToolArgsThresholdRatio = 0,
                    TruncateToolArgsKeepMessages = 10,
                    TruncateToolArgsMaxChars = 2000
                },
                Session = new SessionConfig { MaxHistoryTurns = 200, MinTurnsToKeep = 14, TimeoutMinutes = 30, CleanupIntervalMinutes = 5 }
            },
            new ContextOptimizationPreset
            {
                Id = "pass-through",
                DisplayName = "完全依赖模型",
                ContextWindow = new ContextWindowConfig
                {
                    PassThroughContext = true,
                    MaxContextTokens = 200_000,
                    ReservedSystemTokens = 16_000,
                    ReservedToolsTokens = 16_000,
                    ReservedOutputTokens = 8_192,
                    PlanContentMaxChars = 0,
                    MemoryInjectionMaxChars = 0,
                    MemorySessionTopK = 0,
                    MemorySharedTopK = 0,
                    TokenEstimation = "CharsRatio",
                    CharsPerToken = 2,
                    SummarizationEnabled = false,
                    SummarizationTriggerRatio = 0.9,
                    SummarizationMaxSummaryChars = 500,
                    ContextLengthRetryEnabled = false,
                    ContextLengthRetryMaxTurns = 0,
                    ConversationHistoryDirectory = null,
                    TruncateToolArgsThresholdRatio = 0,
                    TruncateToolArgsKeepMessages = 10,
                    TruncateToolArgsMaxChars = 2000
                },
                Session = new SessionConfig { MaxHistoryTurns = 5000, MinTurnsToKeep = 8, TimeoutMinutes = 30, CleanupIntervalMinutes = 5 }
            }
        };
    }

    /// <summary>若 ActiveContextPresetId 已设置且存在于 Presets 中，用该预设覆盖 Session/ContextWindow。</summary>
    private static void ApplyActivePresetIfSet(AppConfig config)
    {
        var id = config.ActiveContextPresetId?.Trim();
        if (string.IsNullOrEmpty(id) || config.ContextOptimizationPresets == null) return;
        var preset = config.ContextOptimizationPresets.Find(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (preset == null) return;
        config.Session = new SessionConfig
        {
            MaxHistoryTurns = preset.Session.MaxHistoryTurns,
            MinTurnsToKeep = preset.Session.MinTurnsToKeep,
            TimeoutMinutes = preset.Session.TimeoutMinutes,
            CleanupIntervalMinutes = preset.Session.CleanupIntervalMinutes
        };
        config.ContextWindow = new ContextWindowConfig
        {
            PassThroughContext = preset.ContextWindow.PassThroughContext,
            MaxContextTokens = preset.ContextWindow.MaxContextTokens,
            ReservedSystemTokens = preset.ContextWindow.ReservedSystemTokens,
            ReservedToolsTokens = preset.ContextWindow.ReservedToolsTokens,
            ReservedOutputTokens = preset.ContextWindow.ReservedOutputTokens,
            PlanContentMaxChars = preset.ContextWindow.PlanContentMaxChars,
            MemoryInjectionMaxChars = preset.ContextWindow.MemoryInjectionMaxChars,
            MemorySessionTopK = preset.ContextWindow.MemorySessionTopK,
            MemorySharedTopK = preset.ContextWindow.MemorySharedTopK,
            TokenEstimation = preset.ContextWindow.TokenEstimation,
            CharsPerToken = preset.ContextWindow.CharsPerToken,
            SummarizationEnabled = preset.ContextWindow.SummarizationEnabled,
            SummarizationTriggerRatio = preset.ContextWindow.SummarizationTriggerRatio,
            SummarizationMaxSummaryChars = preset.ContextWindow.SummarizationMaxSummaryChars,
            ContextLengthRetryEnabled = preset.ContextWindow.ContextLengthRetryEnabled,
            ContextLengthRetryMaxTurns = preset.ContextWindow.ContextLengthRetryMaxTurns,
            ConversationHistoryDirectory = preset.ContextWindow.ConversationHistoryDirectory,
            TruncateToolArgsThresholdRatio = preset.ContextWindow.TruncateToolArgsThresholdRatio,
            TruncateToolArgsKeepMessages = preset.ContextWindow.TruncateToolArgsKeepMessages,
            TruncateToolArgsMaxChars = preset.ContextWindow.TruncateToolArgsMaxChars,
            EnableToolNeedGate = preset.ContextWindow.EnableToolNeedGate,
            ToolNeedGateMaxPromptChars = preset.ContextWindow.ToolNeedGateMaxPromptChars
        };
    }

    /// <summary>将旧版顶层 <c>ai</c> 合并进 <see cref="AppConfig.AiModels"/> / <see cref="AppConfig.AlwaysIncludePlugins"/>，然后清空 <see cref="AppConfig.AI"/>（保存时不再写出）。</summary>
    private void MigrateLegacyAiIfNeeded(AppConfig config)
    {
        config.AiModels ??= new List<AiModelEntry>();
        config.AlwaysIncludePlugins ??= new List<string>();

        var legacy = config.AI;
        if (legacy?.AlwaysIncludePlugins is { Count: > 0 } legPlugins && config.AlwaysIncludePlugins.Count == 0)
            config.AlwaysIncludePlugins = new List<string>(legPlugins);

        if (config.AiModels.Count > 0)
        {
            config.AI = null;
            return;
        }

        if (legacy == null) return;

        var provider = (legacy.Provider ?? "").Trim();
        if (string.IsNullOrEmpty(provider)) provider = "OpenAI";
        var entry = new AiModelEntry
        {
            Id = "default",
            DisplayName = "默认模型",
            Provider = provider,
            Endpoint = legacy.Endpoint ?? "",
            ApiKey = legacy.ApiKey ?? "",
            ModelId = legacy.ModelId ?? "gpt-4o-mini",
            SystemPrompt = legacy.SystemPrompt ?? "",
            Enabled = true
        };
        config.AiModels = new List<AiModelEntry> { entry };
        config.ActiveModelId = "default";
        if (config.AlwaysIncludePlugins.Count == 0 && legacy.AlwaysIncludePlugins is { Count: > 0 } lp)
            config.AlwaysIncludePlugins = new List<string>(lp);
        config.AI = null;
        _logger.LogInformation("Migrated legacy AI config to AiModels (Id=default).");
    }

    /// <summary>若 OcrModels 为空且 Ocr 有值，则迁移为一条并设 ActiveOcrModelId。</summary>
    private void MigrateLegacyOcrIfNeeded(AppConfig config)
    {
        if (config.OcrModels == null || config.OcrModels.Count > 0) return;
        var ocr = config.Ocr;
        if (ocr == null || string.IsNullOrWhiteSpace(ocr.Endpoint) || string.IsNullOrWhiteSpace(ocr.ApiKey)) return;
        config.OcrModels = new List<OcrModelEntry>
        {
            new OcrModelEntry
            {
                Id = "ocr-1",
                DisplayName = "OCR",
                Endpoint = ocr.Endpoint.Trim(),
                ApiKey = ocr.ApiKey.Trim(),
                Language = ocr.Language
            }
        };
        config.ActiveOcrModelId = "ocr-1";
        _logger.LogInformation("Migrated legacy Ocr to OcrModels (Id=ocr-1).");
    }

    public void SaveConfig(AppConfig newConfig)
    {
        lock (_lock)
        {
            try
            {
                // 前端可能只提交部分字段，未传的节保留当前值避免被覆盖
                if (newConfig.ContextOptimizationPresets == null) newConfig.ContextOptimizationPresets = _currentConfig.ContextOptimizationPresets ?? new List<ContextOptimizationPreset>();
                if (newConfig.ContextOptimizationPresets.Count == 0) newConfig.ContextOptimizationPresets.AddRange(GetBuiltInPresets());
                if (newConfig.ActiveContextPresetId == null) newConfig.ActiveContextPresetId = _currentConfig.ActiveContextPresetId;
                ApplyActivePresetIfSet(newConfig);
                if (newConfig.Session == null) newConfig.Session = _currentConfig.Session ?? new SessionConfig();
                if (newConfig.ContextWindow == null) newConfig.ContextWindow = _currentConfig.ContextWindow ?? new ContextWindowConfig();
                if (string.IsNullOrWhiteSpace(newConfig.CliRunMode)) newConfig.CliRunMode = _currentConfig.CliRunMode ?? "UseAllowList";
                if (newConfig.AllowedCliCommandsByClient == null) newConfig.AllowedCliCommandsByClient = _currentConfig.AllowedCliCommandsByClient ?? new Dictionary<string, List<string>>();
                if (newConfig.AllowedPageScriptIdsByClient == null) newConfig.AllowedPageScriptIdsByClient = _currentConfig.AllowedPageScriptIdsByClient ?? new Dictionary<string, List<string>>();
                if (newConfig.AllowedDocumentScriptIdsByClient == null) newConfig.AllowedDocumentScriptIdsByClient = _currentConfig.AllowedDocumentScriptIdsByClient ?? new Dictionary<string, List<string>>();
                if (newConfig.EmbeddingModels == null) newConfig.EmbeddingModels = _currentConfig.EmbeddingModels ?? new List<EmbeddingModelEntry>();
                else
                    PreserveEmbeddingEndpointsFromCurrent(newConfig.EmbeddingModels, _currentConfig.EmbeddingModels);
                if (newConfig.ActiveEmbeddingModelId == null) newConfig.ActiveEmbeddingModelId = _currentConfig.ActiveEmbeddingModelId;
                if (newConfig.OcrModels == null) newConfig.OcrModels = _currentConfig.OcrModels ?? new List<OcrModelEntry>();
                if (newConfig.ActiveOcrModelId == null) newConfig.ActiveOcrModelId = _currentConfig.ActiveOcrModelId;
                if (string.IsNullOrWhiteSpace(newConfig.UiThemeId)) newConfig.UiThemeId = _currentConfig.UiThemeId;
                if (newConfig.WebSocketAuthToken == null) newConfig.WebSocketAuthToken = _currentConfig.WebSocketAuthToken;
                if (newConfig.SemanticKernel == null)
                    newConfig.SemanticKernel = SemanticKernelFeaturesConfig.Clone(_currentConfig.SemanticKernel);
                if (newConfig.AiModels == null)
                    newConfig.AiModels = _currentConfig.AiModels != null
                        ? new List<AiModelEntry>(_currentConfig.AiModels)
                        : new List<AiModelEntry>();
                if (string.IsNullOrWhiteSpace(newConfig.ActiveModelId))
                    newConfig.ActiveModelId = _currentConfig.ActiveModelId ?? "";
                if (newConfig.AlwaysIncludePlugins == null)
                    newConfig.AlwaysIncludePlugins = _currentConfig.AlwaysIncludePlugins ?? new List<string>();
                MigrateLegacyAiIfNeeded(newConfig);
                newConfig.AI = null;
                var activeEmbId = (newConfig.ActiveEmbeddingModelId ?? "").Trim();
                if (!string.IsNullOrEmpty(activeEmbId) && (newConfig.EmbeddingModels == null || newConfig.EmbeddingModels.All(e => (e.Id ?? "").Trim() != activeEmbId)))
                {
                    _logger.LogWarning("ActiveEmbeddingModelId \"{Id}\" not found in EmbeddingModels list, clearing.", activeEmbId);
                    newConfig.ActiveEmbeddingModelId = null;
                }
                var activeOcrId = (newConfig.ActiveOcrModelId ?? "").Trim();
                if (!string.IsNullOrEmpty(activeOcrId) && (newConfig.OcrModels == null || newConfig.OcrModels.All(e => (e.Id ?? "").Trim() != activeOcrId)))
                {
                    _logger.LogWarning("ActiveOcrModelId \"{Id}\" not found in OcrModels list, clearing.", activeOcrId);
                    newConfig.ActiveOcrModelId = null;
                }
                // 与 JsonCtx 一致使用 CamelCase，否则下次反序列化时字段对不上
                var options = new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    TypeInfoResolver = JsonCtx.Default
                };
                var json = JsonSerializer.Serialize(newConfig, typeof(AppConfig), options);
                File.WriteAllText(_configPath, json);
                _currentConfig = newConfig;
                _logger.LogInformation("User config saved to {Path} (length {Len})", _configPath, json.Length);
                OnConfigChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save user config.");
                throw;
            }
        }
    }
}
