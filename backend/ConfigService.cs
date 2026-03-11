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
}

public class AppConfig
{
    public AiConfig AI { get; set; } = new();
    public List<McpServerConfig> McpServers { get; set; } = new();
    /// <summary>MCP 工具 run_page_script 允许执行的脚本 ID 白名单；空则使用默认列表。</summary>
    public List<string> AllowedPageScriptIds { get; set; } = new();
    /// <summary>CLI 工具 run_command 允许执行的命令白名单（每项为命令名，如 dir、echo、type）；空则使用默认列表。</summary>
    public List<string> AllowedCliCommands { get; set; } = new();
    /// <summary>已停用的内置插件 ID 列表（如 Browser、File、CLI、Excel、Word），这些插件不会注册到 Kernel。</summary>
    public List<string> DisabledBuiltInPlugins { get; set; } = new();
    /// <summary>RunEverything 模式：为 true 时所有会话下 run_command / run_page_script 不校验白名单、不发起 HITL，直接放行。</summary>
    public bool RunEverythingMode { get; set; }
}

public sealed class ConfigService
{
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "user-config.json");
    private AppConfig _currentConfig;
    private readonly ILogger<ConfigService> _logger;
    private readonly object _lock = new();

    public event Action? OnConfigChanged;

    public ConfigService(IConfiguration defaultConfig, ILogger<ConfigService> logger)
    {
        _logger = logger;
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
                if (config != null)
                {
                    _logger.LogInformation("Loaded user config from {Path}", _configPath);
                    return config;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user config. Falling back to default.");
            }
        }

        // Fallback to appsettings.json
        var appConfig = new AppConfig();
        defaultConfig.GetSection("AI").Bind(appConfig.AI);
        return appConfig;
    }

    public void SaveConfig(AppConfig newConfig)
    {
        lock (_lock)
        {
            try
            {
                // 可读格式：中文等不转成 \uXXXX，并缩进
                var options = new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true,
                    TypeInfoResolver = JsonCtx.Default
                };
                var json = JsonSerializer.Serialize(newConfig, typeof(AppConfig), options);
                File.WriteAllText(_configPath, json);
                _currentConfig = newConfig;
                _logger.LogInformation("User config saved to {Path}", _configPath);
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
