using System.Text.Encodings.Web;
using System.Text.Json;
using OfficeCopilot.Server.Mcp;

namespace OfficeCopilot.Server;

public class AiConfig
{
    public string Provider { get; set; } = "OpenAI";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "gpt-4o-mini";
    public string SystemPrompt { get; set; } = "你是 Office Copilot，一个智能办公自动化助手。你运行在用户的本地电脑上，能够帮助用户操作 Excel、Word 文档，执行系统命令。请用简洁友好的中文回答用户问题。\n如果用户让你画图、展示报表或动态页面，请直接返回一段完整的、带有 <html_canvas> 和 </html_canvas> 标签包裹的 HTML 代码（里面可以引入 Echarts 或其他 CDN 图表库），我会用浏览器渲染给用户看。";
    /// <summary>工具选择专用模型 Id，对应 AiModels 中某条；为空则使用嵌入式小模型或当前主模型兜底。</summary>
    public string? ToolSelectionModelId { get; set; }
    /// <summary>始终包含的插件名（如 CLI），即使用户未提到也会传给模型。</summary>
    public List<string> AlwaysIncludePlugins { get; set; } = new();
    /// <summary>为 true 时使用两阶段工具选择（一阶段选子类、二阶段选函数）；为 false 时退化为单阶段选插件。null 视为 true。</summary>
    public bool? ToolSelectionTwoStage { get; set; } = true;
}

/// <summary>多模型列表中的单条：支持 OpenAI / Azure / Ollama / Anthropic。</summary>
public class AiModelEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>类型：OpenAI, Azure, Ollama, Anthropic。</summary>
    public string Provider { get; set; } = "OpenAI";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "";
    /// <summary>Azure 部署名；仅 Provider=Azure 时使用。</summary>
    public string DeploymentName { get; set; } = "";
    /// <summary>Azure API 版本；仅 Provider=Azure 时使用。</summary>
    public string ApiVersion { get; set; } = "2024-02-01";
    /// <summary>可选；不填则使用 AI.SystemPrompt 全局默认。</summary>
    public string SystemPrompt { get; set; } = "";
    public bool Enabled { get; set; } = true;
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
    /// <summary>嵌入式工具选择模型路径（GGUF 文件）；为空时使用运行目录下 Models/ 中默认文件名。设置页「本地兜底模型」列表中启用某条时写入。</summary>
    public string? EmbeddedToolSelectionModelPath { get; set; }
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
