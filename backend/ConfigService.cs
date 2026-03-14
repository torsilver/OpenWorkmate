using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeCopilot.Server.Mcp;

namespace OfficeCopilot.Server;

public class AiConfig
{
    public string Provider { get; set; } = "OpenAI";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "gpt-4o-mini";
    public string SystemPrompt { get; set; } = "你是 Office Copilot，一个智能办公自动化助手。你运行在用户的本地电脑上，能够帮助用户操作 Excel、Word 文档，执行系统命令。请用简洁友好的中文回答用户问题。\n如果用户让你画图、展示报表或动态页面，请直接返回一段完整的、带有 <html_canvas> 和 </html_canvas> 标签包裹的 HTML 代码（里面可以引入 Echarts 或其他 CDN 图表库），我会用浏览器渲染给用户看。\n用户可能从浏览器侧边栏、Word/Excel 任务窗格或 WPS 加载项连接：操作当前打开的文档请用 CurrentDocument（仅任务窗格/WPS 端可用），操作网页高亮与截图请用 Browser（仅浏览器端可用）；若当前端不支持会返回提示，可引导用户切换到对应端。";
    /// <summary>始终包含的插件名（如 CLI），即使用户未提到也会传给模型。</summary>
    public List<string> AlwaysIncludePlugins { get; set; } = new();
    /// <summary>为 true 时使用两阶段工具选择（一阶段选子类、二阶段选函数）；为 false 时退化为单阶段选插件。null 视为 true。</summary>
    public bool? ToolSelectionTwoStage { get; set; } = true;
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

public class AppConfig
{
    public AiConfig AI { get; set; } = new();
    /// <summary>会话配置（历史轮数、超时等）；未配置时使用默认值。</summary>
    public SessionConfig? Session { get; set; }
    /// <summary>上下文窗口配置（64K 优化、预留、摘要、重试等）；未配置时使用默认值。</summary>
    public ContextWindowConfig? ContextWindow { get; set; }
    /// <summary>计划确认规则（步数阈值、敏感工具等）；未配置时使用默认值。</summary>
    public PlanConfirmationConfig? PlanConfirmation { get; set; }
    /// <summary>多套 AI 模型列表；为空时使用 AI 单条配置。</summary>
    public List<AiModelEntry> AiModels { get; set; } = new();
    /// <summary>当前使用的模型 Id，对应 AiModels 中某条的 Id。</summary>
    public string ActiveModelId { get; set; } = "";
    public List<McpServerConfig> McpServers { get; set; } = new();
    /// <summary>MCP 工具 run_page_script 允许执行的脚本 ID 白名单；空则使用默认列表。</summary>
    public List<string> AllowedPageScriptIds { get; set; } = new();
    /// <summary>CLI 工具 run_command 允许执行的命令白名单（每项为命令名，如 dir、echo、type）；空则使用默认列表。</summary>
    public List<string> AllowedCliCommands { get; set; } = new();
    /// <summary>已停用的内置插件 ID 列表（如 Browser、File、CLI、Excel、Word），这些插件不会注册到 Kernel。</summary>
    public List<string> DisabledBuiltInPlugins { get; set; } = new();
    /// <summary>RunEverything 模式：为 true 时所有会话下 run_command / run_page_script 不校验白名单、不发起 HITL，直接放行。</summary>
    public bool RunEverythingMode { get; set; }
    /// <summary>Tavily API Key，用于网页搜索技能；也可通过环境变量 TAVILY_API_KEY 提供。</summary>
    public string TavilyApiKey { get; set; } = "";
    /// <summary>技能所需环境变量统一配置：键为环境变量名（如 TAVILY_API_KEY、OPENAI_API_KEY），值为配置内容。执行 Clawhub 脚本时优先从此处读取，若无则从系统环境变量读取。可在设置页或 user-config.json 中配置。</summary>
    public Dictionary<string, string> SkillEnv { get; set; } = new();
    // ----- 阶段 3：嵌入与 RAG / 记忆 -----
    /// <summary>Embedding 来源：仅支持 Remote（远程 API）；不配置则记忆/RAG 不生效。</summary>
    public string EmbeddingSource { get; set; } = "";
    /// <summary>远程 Embedding 接口地址；EmbeddingSource=Remote 时使用，与大模型配置独立。</summary>
    public string? EmbeddingEndpoint { get; set; }
    /// <summary>远程 Embedding API Key；EmbeddingSource=Remote 时使用。</summary>
    public string? EmbeddingApiKey { get; set; }
    /// <summary>远程 Embedding 模型名（如 text-embedding-3-small）；EmbeddingSource=Remote 时使用。</summary>
    public string? EmbeddingModelId { get; set; }
    /// <summary>向量存储类型：Memory = 内存，Sqlite = 本地 db 文件。</summary>
    public string RagStorageType { get; set; } = "Memory";
    /// <summary>SQLite 向量库路径（RagStorageType=Sqlite 时）；为空时使用 %LocalAppData%/OfficeCopilot/rag.db。</summary>
    public string? RagStoragePath { get; set; }
    /// <summary>计划存储目录（.plan.md 文件）；为空时使用 %LocalAppData%/OfficeCopilot/Plans。</summary>
    public string? PlansDirectory { get; set; }
    /// <summary>准确数据 MCP 存储目录；为空时使用 %LocalAppData%/OfficeCopilot/AccurateData。启动 accurate-data-mcp 时会通过环境变量 ACCURATE_DATA_DIRECTORY 传入。</summary>
    public string? AccurateDataDirectory { get; set; }
    /// <summary>定时任务存储目录（.task.md 文件）；为空时使用 %LocalAppData%/OfficeCopilot/ScheduledTasks。</summary>
    public string? ScheduledTasksDirectory { get; set; }
}

public sealed class ConfigService
{
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

    private AppConfig LoadConfig(IConfiguration defaultConfig)
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonCtx.Default.AppConfig);
                if (config != null && config.AI != null)
                {
                    _logger.LogInformation("Loaded user config from {Path}", _configPath);
                    if (string.IsNullOrWhiteSpace(config.TavilyApiKey))
                        config.TavilyApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
                    config.SkillEnv ??= new Dictionary<string, string>();
                    config.AiModels ??= new List<AiModelEntry>();
                    config.Session ??= new SessionConfig();
                    config.ContextWindow ??= new ContextWindowConfig();
                    config.PlanConfirmation ??= new PlanConfirmationConfig();
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
        if (string.IsNullOrWhiteSpace(appConfig.TavilyApiKey))
            appConfig.TavilyApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
        appConfig.SkillEnv ??= new Dictionary<string, string>();
        appConfig.AiModels ??= new List<AiModelEntry>();
        MigrateLegacyAiIfNeeded(appConfig);
        return appConfig;
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
                if (newConfig.Session == null) newConfig.Session = _currentConfig.Session ?? new SessionConfig();
                if (newConfig.ContextWindow == null) newConfig.ContextWindow = _currentConfig.ContextWindow ?? new ContextWindowConfig();
                if (newConfig.PlanConfirmation == null) newConfig.PlanConfirmation = _currentConfig.PlanConfirmation ?? new PlanConfirmationConfig();
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
