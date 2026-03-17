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
    public string SystemPrompt { get; set; } = "你是 Office Copilot，一个智能办公自动化助手。你运行在用户的本地电脑上，能够帮助用户操作 Excel、Word 文档，执行系统命令。请用简洁友好的中文回答用户问题。\n如果用户让你画图、展示报表或动态页面，请直接返回一段完整的、带有 <html_canvas> 和 </html_canvas> 标签包裹的 HTML 代码（里面可以引入 Echarts 或其他 CDN 图表库），我会用浏览器渲染给用户看。\n用户可能从浏览器侧边栏、Word/Excel 任务窗格或 WPS 加载项连接：操作当前打开的文档请用 CurrentDocument（仅任务窗格/WPS 端可用），操作网页高亮与截图请用 Browser（仅浏览器端可用）；若当前端不支持会返回提示，可引导用户切换到对应端。\n当你获得大量结构化数据、表格或需要跨步骤精确引用的内容时，请使用 accurate_data_write 保存，之后用 accurate_data_read 按 id 取回，避免占用对话上下文。\n创建 Word 文档时，paragraphs 中请用 Markdown 格式：以 # / ## / ### 开头的段落会自动变为对应级别标题，以 - 开头的段落会自动变为列表项，其余为正文段落（自动带首行缩进和专业排版）。创建 PPT 时，bodyText 中用换行分段，以 - 开头的行会自动变为项目符号。在当前文档中插入文字时，可用 style 参数指定样式（如 Heading1、Heading2）使文档结构清晰。";
    /// <summary>始终包含的插件名（如 CLI），即使用户未提到也会传给模型。</summary>
    public List<string> AlwaysIncludePlugins { get; set; } = new();
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
}

/// <summary>会话配置：历史轮数、超时等。</summary>
public class SessionConfig
{
    public int MaxHistoryTurns { get; set; } = 80;
    public int MinTurnsToKeep { get; set; } = 8;
    public int TimeoutMinutes { get; set; } = 30;
    public int CleanupIntervalMinutes { get; set; } = 5;
}

/// <summary>计划确认规则：由后台规则决定计划是否需要用户确认后再执行；步数阈值与敏感工具可配置。</summary>
public class PlanConfirmationConfig
{
    /// <summary>步数 ≤ 该值时可不确认直接执行；大于则需确认。默认 3。</summary>
    public int AutoExecuteMaxSteps { get; set; } = 3;
    /// <summary>是否启用「涉及敏感工具则必须确认」。</summary>
    public bool RequireConfirmForSensitiveTools { get; set; }
    /// <summary>触发确认的工具标识（如 "CLI:run_command"、插件名:函数名），空则仅按步数判断。</summary>
    public List<string> SensitiveToolIds { get; set; } = new();
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

    /// <summary>工具向量检索：返回最多 topK 个结果。</summary>
    public int ToolSearchTopK { get; set; } = 20;
    /// <summary>工具向量检索：最高分 >= 此值时认为结果可用。</summary>
    public double ToolSearchMinScore { get; set; } = 0.7;
    /// <summary>工具向量检索：至少命中此数量才算 goodEnough。</summary>
    public int ToolSearchMinCount { get; set; } = 1;

    /// <summary>完全不优化（完全依赖大模型）：为 true 时不按 token 裁历史、不摘要、不截断工具参数、不触发超长重试；仅保留轮数上限。为 false 时使用本配置内其余优化参数。</summary>
    public bool PassThroughContext { get; set; }
}

/// <summary>上下文优化预设：一组 ContextWindow + Session + PlanConfirmation，用于切换「公司内部 64K」「Kimi K2.5」或自定义。</summary>
public class ContextOptimizationPreset
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ContextWindowConfig ContextWindow { get; set; } = new();
    public SessionConfig Session { get; set; } = new();
    public PlanConfirmationConfig PlanConfirmation { get; set; } = new();
}

/// <summary>测试 AI 连接时前端传入的请求体。</summary>
public class TestAiRequest
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? ModelId { get; set; }
    public string? Provider { get; set; }
    public string? DeploymentName { get; set; }
}

/// <summary>测试 Embedding 连接时前端传入的请求体。</summary>
public class TestEmbeddingRequest
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? ModelId { get; set; }
}

/// <summary>语音转文字（STT）配置，用于 POST /api/transcribe；不配置时使用当前 AI 模型的 endpoint/apiKey 调用 Whisper。</summary>
public class SpeechToTextConfig
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    /// <summary>可选语言代码，如 zh、en；空则自动检测。</summary>
    public string? Language { get; set; }
    /// <summary>长音频分片时长（分钟），超过 25MB 时按此分片后逐段调用；默认 2。</summary>
    public int ChunkMinutes { get; set; } = 2;
}

/// <summary>OCR 配置，用于内置 MCP_OCR 工具；调用远程 OCR API（如 Azure Document Intelligence 或兼容接口）。</summary>
public class OcrConfig
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>可选语言或模型标识，视具体 API 而定。</summary>
    public string? Language { get; set; }
}

public class AppConfig
{
    public AiConfig AI { get; set; } = new();
    /// <summary>会话配置（历史轮数、超时等）；未配置时使用默认值。</summary>
    public SessionConfig? Session { get; set; }
    /// <summary>上下文窗口配置（64K 优化、预留、摘要、重试等）；未配置时使用默认值。</summary>
    public ContextWindowConfig? ContextWindow { get; set; }
    /// <summary>计划确认规则（步数阈值、敏感工具等）；未配置时使用默认值。</summary>
    public PlanConfirmationConfig? PlanConfirmation { get; set; }
    /// <summary>上下文优化预设列表（内置 64K/Kimi K2.5 + 用户自定义）；空时在加载时注入内置两条。</summary>
    public List<ContextOptimizationPreset>? ContextOptimizationPresets { get; set; }
    /// <summary>当前生效的预设 Id；非空且存在于 Presets 时，加载后用该预设覆盖 Session/ContextWindow/PlanConfirmation。</summary>
    public string? ActiveContextPresetId { get; set; }
    /// <summary>多套 AI 模型列表；为空时使用 AI 单条配置。</summary>
    public List<AiModelEntry> AiModels { get; set; } = new();
    /// <summary>当前使用的模型 Id，对应 AiModels 中某条的 Id。</summary>
    public string ActiveModelId { get; set; } = "";
    public List<McpServerConfig> McpServers { get; set; } = new();
    /// <summary>按端（chrome/backend/office/wps）CLI 与页面脚本运行模式：RunEverything | AskEverytime | UseAllowList。未配置的端默认 UseAllowList。</summary>
    public Dictionary<string, string> CliRunModeByClient { get; set; } = new();
    /// <summary>按端命令白名单（每项为命令名，如 dir、echo、type）；空则使用默认列表。</summary>
    public Dictionary<string, List<string>> AllowedCliCommandsByClient { get; set; } = new();
    /// <summary>按端页面脚本 ID 白名单；空则使用默认列表。</summary>
    public Dictionary<string, List<string>> AllowedPageScriptIdsByClient { get; set; } = new();
    /// <summary>已停用的内置插件 ID 列表（如 Browser、File、CLI、Excel、Word），这些插件不会注册到 Kernel。</summary>
    public List<string> DisabledBuiltInPlugins { get; set; } = new();
    /// <summary>Tavily API Key，用于网页搜索技能；也可通过环境变量 TAVILY_API_KEY 提供。</summary>
    public string TavilyApiKey { get; set; } = "";
    /// <summary>技能所需环境变量统一配置：键为环境变量名（如 TAVILY_API_KEY、OPENAI_API_KEY），值为配置内容。执行 Clawhub 脚本时优先从此处读取，若无则从系统环境变量读取。可在设置页或 user-config.json 中配置。</summary>
    public Dictionary<string, string> SkillEnv { get; set; } = new();
    // ----- 阶段 3：嵌入与 RAG / 记忆 -----
    /// <summary>Embedding 模型列表；支持多条，仅 Remote 远程 API。</summary>
    public List<EmbeddingModelEntry> EmbeddingModels { get; set; } = new();
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
    /// <summary>语音转文字（Whisper）配置；为空时使用当前 AI 模型的 endpoint/apiKey。</summary>
    public SpeechToTextConfig? SpeechToText { get; set; }
    /// <summary>OCR 配置；为空时 MCP_OCR 工具不可用。</summary>
    public OcrConfig? Ocr { get; set; }
}

/// <summary>四端键名：chrome、backend、office、wps。</summary>
public static class CliScriptEndKeys
{
    public const string Chrome = "chrome";
    public const string Backend = "backend";
    public const string Office = "office";
    public const string Wps = "wps";

    public static readonly string[] DefaultAllowedCommands = { "dir", "echo", "type", "ping", "systeminfo", "ipconfig" };
    public static readonly string[] DefaultAllowedScriptIds = { "scroll_to_top", "scroll_to_bottom", "get_visible_text", "get_page_title" };

    /// <summary>将 clientType 解析为四端之一。</summary>
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

    public ConfigService(IConfiguration defaultConfig, ILogger<ConfigService> logger)
    {
        _logger = logger;
        // 使用用户本地应用数据目录，与运行目录无关，避免 dotnet run/clean 后配置丢失
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData) || !Path.IsPathRooted(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? AppContext.BaseDirectory;
        var appDir = Path.Combine(appData, "OfficeCopilot");
        try { Directory.CreateDirectory(appDir); } catch { /* 无权限时后续写文件会报错 */ }
        _configPath = Path.Combine(appDir, "user-config.json");
        _logger.LogInformation("Config file path: {Path} (exists: {Exists})", _configPath, File.Exists(_configPath));
        _currentConfig = LoadConfig(defaultConfig);
    }

    public AppConfig Current => _currentConfig;

    /// <summary>获取指定端的 CLI/脚本运行模式，未配置时默认 UseAllowList。</summary>
    public string GetCliRunModeForEnd(string endKey)
    {
        var mode = _currentConfig.CliRunModeByClient?.GetValueOrDefault(endKey)?.Trim();
        return string.IsNullOrEmpty(mode) ? "UseAllowList" : mode;
    }

    /// <summary>获取指定端的命令白名单；空或未配置时返回 null（调用方使用默认列表）。</summary>
    public IReadOnlyList<string>? GetAllowedCliCommandsForEnd(string endKey)
    {
        if (_currentConfig.AllowedCliCommandsByClient == null) return null;
        if (!_currentConfig.AllowedCliCommandsByClient.TryGetValue(endKey, out var list) || list == null || list.Count == 0)
            return null;
        return list;
    }

    /// <summary>获取指定端的页面脚本白名单；空或未配置时返回 null（调用方使用默认列表）。</summary>
    public IReadOnlyList<string>? GetAllowedPageScriptIdsForEnd(string endKey)
    {
        if (_currentConfig.AllowedPageScriptIdsByClient == null) return null;
        if (!_currentConfig.AllowedPageScriptIdsByClient.TryGetValue(endKey, out var list) || list == null || list.Count == 0)
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

    private AppConfig LoadConfig(IConfiguration defaultConfig)
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigDeserializeOptions);
                if (config != null && config.AI != null)
                {
                    PatchEmbeddingEndpointsFromRawJson(json, config);
                    _logger.LogInformation("Loaded user config from {Path}", _configPath);
                    if (string.IsNullOrWhiteSpace(config.TavilyApiKey))
                        config.TavilyApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
                    config.SkillEnv ??= new Dictionary<string, string>();
                    config.CliRunModeByClient ??= new Dictionary<string, string>();
                    config.AllowedCliCommandsByClient ??= new Dictionary<string, List<string>>();
                    config.AllowedPageScriptIdsByClient ??= new Dictionary<string, List<string>>();
                    config.AiModels ??= new List<AiModelEntry>();
                    config.EmbeddingModels ??= new List<EmbeddingModelEntry>();
                    config.Session ??= new SessionConfig();
                    config.ContextWindow ??= new ContextWindowConfig();
                    config.PlanConfirmation ??= new PlanConfirmationConfig();
                    config.ContextOptimizationPresets ??= new List<ContextOptimizationPreset>();
                    if (config.ContextOptimizationPresets.Count == 0)
                    {
                        var fromConfig = LoadPresetsFromConfiguration(defaultConfig);
                        if (fromConfig != null && fromConfig.Count > 0)
                            config.ContextOptimizationPresets.AddRange(fromConfig);
                        else
                            config.ContextOptimizationPresets.AddRange(GetBuiltInPresets());
                    }
                    ApplyActivePresetIfSet(config);
                    MigrateLegacyAiIfNeeded(config);
                    return config;
                }
                _logger.LogWarning("User config file was empty or invalid, using default.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user config. Falling back to default.");
            }
        }
        else
        {
            _logger.LogInformation("No user config file at {Path}, using default.", _configPath);
        }

        // Fallback to appsettings.json
        var appConfig = new AppConfig();
        defaultConfig.GetSection("AI").Bind(appConfig.AI);
        appConfig.Session = new SessionConfig();
        defaultConfig.GetSection("Session").Bind(appConfig.Session);
        appConfig.ContextWindow = new ContextWindowConfig();
        defaultConfig.GetSection("ContextWindow").Bind(appConfig.ContextWindow);
        appConfig.PlanConfirmation = new PlanConfirmationConfig();
        defaultConfig.GetSection("PlanConfirmation").Bind(appConfig.PlanConfirmation);
        var presetsFromConfig = LoadPresetsFromConfiguration(defaultConfig);
        appConfig.ContextOptimizationPresets = (presetsFromConfig != null && presetsFromConfig.Count > 0)
            ? new List<ContextOptimizationPreset>(presetsFromConfig)
            : new List<ContextOptimizationPreset>(GetBuiltInPresets());
        if (string.IsNullOrWhiteSpace(appConfig.ActiveContextPresetId))
            appConfig.ActiveContextPresetId = "internal-64k";
        ApplyActivePresetIfSet(appConfig);
        if (string.IsNullOrWhiteSpace(appConfig.TavilyApiKey))
            appConfig.TavilyApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
        appConfig.SkillEnv ??= new Dictionary<string, string>();
        appConfig.AiModels ??= new List<AiModelEntry>();
        appConfig.EmbeddingModels ??= new List<EmbeddingModelEntry>();
        MigrateLegacyAiIfNeeded(appConfig);
        return appConfig;
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

    /// <summary>从 IConfiguration 的 ContextOptimizationPresets 节点加载预设列表；若节点为空或绑定失败则返回 null。</summary>
    private static List<ContextOptimizationPreset>? LoadPresetsFromConfiguration(IConfiguration defaultConfig)
    {
        var section = defaultConfig.GetSection("ContextOptimizationPresets");
        if (section == null || !section.GetChildren().Any()) return null;
        var list = new List<ContextOptimizationPreset>();
        foreach (var child in section.GetChildren())
        {
            var preset = new ContextOptimizationPreset();
            try
            {
                child.Bind(preset);
                if (preset.ContextWindow == null) preset.ContextWindow = new ContextWindowConfig();
                if (preset.Session == null) preset.Session = new SessionConfig();
                if (preset.PlanConfirmation == null) preset.PlanConfirmation = new PlanConfirmationConfig();
                list.Add(preset);
            }
            catch
            {
                return null;
            }
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>内置预设：公司内部 64K、Kimi K2.5（256K）。</summary>
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
                    TruncateToolArgsMaxChars = 2000,
                    ToolSearchTopK = 20,
                    ToolSearchMinScore = 0.7,
                    ToolSearchMinCount = 1
                },
                Session = new SessionConfig { MaxHistoryTurns = 80, MinTurnsToKeep = 8, TimeoutMinutes = 30, CleanupIntervalMinutes = 5 },
                PlanConfirmation = new PlanConfirmationConfig { AutoExecuteMaxSteps = 3, RequireConfirmForSensitiveTools = false, SensitiveToolIds = new List<string>() }
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
                    TruncateToolArgsMaxChars = 2000,
                    ToolSearchTopK = 20,
                    ToolSearchMinScore = 0.7,
                    ToolSearchMinCount = 1
                },
                Session = new SessionConfig { MaxHistoryTurns = 150, MinTurnsToKeep = 12, TimeoutMinutes = 30, CleanupIntervalMinutes = 5 },
                PlanConfirmation = new PlanConfirmationConfig { AutoExecuteMaxSteps = 3, RequireConfirmForSensitiveTools = false, SensitiveToolIds = new List<string>() }
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
                    TruncateToolArgsMaxChars = 2000,
                    ToolSearchTopK = 20,
                    ToolSearchMinScore = 0.7,
                    ToolSearchMinCount = 1
                },
                Session = new SessionConfig { MaxHistoryTurns = 5000, MinTurnsToKeep = 8, TimeoutMinutes = 30, CleanupIntervalMinutes = 5 },
                PlanConfirmation = new PlanConfirmationConfig { AutoExecuteMaxSteps = 3, RequireConfirmForSensitiveTools = false, SensitiveToolIds = new List<string>() }
            }
        };
    }

    /// <summary>若 ActiveContextPresetId 已设置且存在于 Presets 中，用该预设覆盖 Session/ContextWindow/PlanConfirmation。</summary>
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
            ToolSearchTopK = preset.ContextWindow.ToolSearchTopK,
            ToolSearchMinScore = preset.ContextWindow.ToolSearchMinScore,
            ToolSearchMinCount = preset.ContextWindow.ToolSearchMinCount
        };
        config.PlanConfirmation = new PlanConfirmationConfig
        {
            AutoExecuteMaxSteps = preset.PlanConfirmation.AutoExecuteMaxSteps,
            RequireConfirmForSensitiveTools = preset.PlanConfirmation.RequireConfirmForSensitiveTools,
            SensitiveToolIds = preset.PlanConfirmation.SensitiveToolIds != null ? new List<string>(preset.PlanConfirmation.SensitiveToolIds) : new List<string>()
        };
    }

    /// <summary>若 AiModels 为空且 AI 有配置，则迁移为一条默认模型并设 ActiveModelId。</summary>
    private void MigrateLegacyAiIfNeeded(AppConfig config)
    {
        if (config.AiModels == null || config.AiModels.Count > 0) return;
        var ai = config.AI;
        if (ai == null) return;
        var provider = (ai.Provider ?? "").Trim();
        if (string.IsNullOrEmpty(provider)) provider = "OpenAI";
        var entry = new AiModelEntry
        {
            Id = "default",
            DisplayName = "默认模型",
            Provider = provider,
            Endpoint = ai.Endpoint ?? "",
            ApiKey = ai.ApiKey ?? "",
            ModelId = ai.ModelId ?? "gpt-4o-mini",
            SystemPrompt = ai.SystemPrompt ?? "",
            Enabled = true
        };
        config.AiModels = new List<AiModelEntry> { entry };
        config.ActiveModelId = "default";
        _logger.LogInformation("Migrated legacy AI config to AiModels (Id=default).");
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
                if (newConfig.PlanConfirmation == null) newConfig.PlanConfirmation = _currentConfig.PlanConfirmation ?? new PlanConfirmationConfig();
                if (newConfig.CliRunModeByClient == null) newConfig.CliRunModeByClient = _currentConfig.CliRunModeByClient ?? new Dictionary<string, string>();
                if (newConfig.AllowedCliCommandsByClient == null) newConfig.AllowedCliCommandsByClient = _currentConfig.AllowedCliCommandsByClient ?? new Dictionary<string, List<string>>();
                if (newConfig.AllowedPageScriptIdsByClient == null) newConfig.AllowedPageScriptIdsByClient = _currentConfig.AllowedPageScriptIdsByClient ?? new Dictionary<string, List<string>>();
                if (newConfig.EmbeddingModels == null) newConfig.EmbeddingModels = _currentConfig.EmbeddingModels ?? new List<EmbeddingModelEntry>();
                else
                    PreserveEmbeddingEndpointsFromCurrent(newConfig.EmbeddingModels, _currentConfig.EmbeddingModels);
                if (newConfig.ActiveEmbeddingModelId == null) newConfig.ActiveEmbeddingModelId = _currentConfig.ActiveEmbeddingModelId;
                if (newConfig.SpeechToText == null) newConfig.SpeechToText = _currentConfig.SpeechToText;
                if (newConfig.Ocr == null) newConfig.Ocr = _currentConfig.Ocr;
                var activeEmbId = (newConfig.ActiveEmbeddingModelId ?? "").Trim();
                if (!string.IsNullOrEmpty(activeEmbId) && (newConfig.EmbeddingModels == null || newConfig.EmbeddingModels.All(e => (e.Id ?? "").Trim() != activeEmbId)))
                {
                    _logger.LogWarning("ActiveEmbeddingModelId \"{Id}\" not found in EmbeddingModels list, clearing.", activeEmbId);
                    newConfig.ActiveEmbeddingModelId = null;
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
