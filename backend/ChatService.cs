using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OfficeCopilot.Server.Filters;
using OfficeCopilot.Server.Plugins;

using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Mcp;

namespace OfficeCopilot.Server;

public sealed class ChatService : IDisposable
{
    private Kernel _kernel = null!;
    private IChatCompletionService _chat = null!;
    private readonly int _maxTurns;
    private readonly int _timeoutMinutes;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly ILogger<ChatService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConfigService _configService;
    private readonly SkillService _skillService;
    private readonly McpClientManager _mcpManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly object _kernelLock = new();

    public ChatService(IConfiguration config, ILogger<ChatService> logger, ILoggerFactory loggerFactory, ConfigService configService, SkillService skillService, McpClientManager mcpManager, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configService = configService;
        _skillService = skillService;
        _mcpManager = mcpManager;
        _serviceProvider = serviceProvider;

        var session = config.GetSection("Session");
        _maxTurns = session.GetValue("MaxHistoryTurns", 50);
        _timeoutMinutes = session.GetValue("TimeoutMinutes", 30);
        var cleanupInterval = session.GetValue("CleanupIntervalMinutes", 5);

        RebuildKernelAsync().GetAwaiter().GetResult();
        _configService.OnConfigChanged += () => _ = RebuildKernelAsync();
        _skillService.OnSkillsChanged += () => _ = RebuildKernelAsync();

        _cleanupTimer = new Timer(CleanupExpiredSessions, null,
            TimeSpan.FromMinutes(cleanupInterval),
            TimeSpan.FromMinutes(cleanupInterval));
    }

    private async Task RebuildKernelAsync()
    {
        var ai = _configService.Current.AI;
        var builder = Kernel.CreateBuilder();

        if (!string.IsNullOrEmpty(ai.Endpoint) && ai.Endpoint != "https://api.openai.com")
        {
            builder.AddOpenAIChatCompletion(
                modelId: ai.ModelId,
                apiKey: ai.ApiKey,
                endpoint: new Uri(ai.Endpoint));
        }
        else
        {
            builder.AddOpenAIChatCompletion(
                modelId: ai.ModelId,
                apiKey: ai.ApiKey);
        }

        var newKernel = builder.Build();

        // 注册安全拦截器（含 HITL：拦截时发确认请求，用户允许则继续执行）
        var hitlManager = _serviceProvider.GetRequiredService<HitlManager>();
        var securityFilter = new SecurityFilter(_loggerFactory.CreateLogger<SecurityFilter>(), _configService, hitlManager);
        newKernel.FunctionInvocationFilters.Add(securityFilter);
        // 注入当前会话 ID，供 BrowserPlugin 等插件在工具调用时使用
        newKernel.FunctionInvocationFilters.Add(new SessionContextFilter(_loggerFactory.CreateLogger<SessionContextFilter>()));
        // 工具调用状态对前端可见：推送 tool_invocation_start / tool_invocation_end
        var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
        newKernel.FunctionInvocationFilters.Add(new ToolStatusFilter(sessionManager, _loggerFactory.CreateLogger<ToolStatusFilter>()));

        // 已停用的内置插件 ID（不区分大小写）
        var disabledBuiltIn = _configService.Current.DisabledBuiltInPlugins?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .ToHashSet() ?? new HashSet<string>();

        // 注册 Native Plugins（仅未停用的）
        if (!disabledBuiltIn.Contains("cli"))
            newKernel.Plugins.AddFromObject(new CliPlugin(), "CLI");
        if (!disabledBuiltIn.Contains("excel"))
            newKernel.Plugins.AddFromObject(new ExcelPlugin(), "Excel");
        if (!disabledBuiltIn.Contains("word"))
        {
            var wordPluginLogger = _loggerFactory.CreateLogger<WordPlugin>();
            newKernel.Plugins.AddFromObject(new WordPlugin(wordPluginLogger), "Word");
        }

        var rpcManager = _serviceProvider.GetRequiredService<RpcManager>();
        var screenshotCache = _serviceProvider.GetRequiredService<ScreenshotCacheService>();
        var browserPluginLogger = _loggerFactory.CreateLogger<BrowserPlugin>();
        var filePluginLogger = _loggerFactory.CreateLogger<FilePlugin>();
        if (!disabledBuiltIn.Contains("browser"))
            newKernel.Plugins.AddFromObject(new BrowserPlugin(sessionManager, rpcManager, screenshotCache, browserPluginLogger), "Browser");
        if (!disabledBuiltIn.Contains("file"))
            newKernel.Plugins.AddFromObject(new FilePlugin(screenshotCache, filePluginLogger), "File");
        
        // 动态注册基于 Prompt 的 Skills
        var userSkills = _skillService.GetAllSkills();
        var skillCount = 0;
        foreach (var skill in userSkills)
        {
            if (!skill.Enabled || string.IsNullOrWhiteSpace(skill.PromptTemplate)) continue;
            
            try 
            {
                var promptConfig = new PromptExecutionSettings { 
                    ExtensionData = new Dictionary<string, object> { 
                        { "max_tokens", 4000 },
                        { "temperature", 0.1 }
                    }
                };
                
                var function = newKernel.CreateFunctionFromPrompt(
                    skill.PromptTemplate,
                    promptConfig,
                    functionName: skill.Id.Replace("-", "_"),
                    description: skill.Description);
                    
                newKernel.Plugins.AddFromFunctions($"UserSkill_{skill.Id.Replace("-", "_")}", new[] { function });
                skillCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register skill {Name}", skill.Name);
            }
        }

        // 动态注册外部 MCP 服务
        var mcpCount = 0;
        foreach (var mcpConfig in _configService.Current.McpServers)
        {
            if (!mcpConfig.Enabled)
                continue;
            try
            {
                var client = await _mcpManager.StartClientAsync(mcpConfig);
                var wrapper = new McpKernelPlugin(client, $"MCP_{mcpConfig.Name}");
                var mcpPlugin = await wrapper.BuildPluginAsync();
                newKernel.Plugins.Add(mcpPlugin);
                mcpCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bind MCP server {Name} to Kernel", mcpConfig.Name);
            }
        }
        
        lock (_kernelLock)
        {
            _kernel = newKernel;
            _chat = _kernel.GetRequiredService<IChatCompletionService>();
        }

        _logger.LogInformation("Kernel rebuilt. Model: {Model}, Plugins: {Count}, UserSkills: {SkillCount}, MCPs: {McpCount}", 
            ai.ModelId, _kernel.Plugins.Count, skillCount, mcpCount);
    }

    public void SetSessionMode(string sessionId, string mode)
    {
        string systemPrompt;
        lock (_kernelLock)
        {
            systemPrompt = _configService.Current.AI.SystemPrompt;
        }
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(systemPrompt));
        state.Mode = mode;
        _logger.LogInformation("[{SessionId}] Mode updated to {Mode} in ChatService", sessionId, mode);
    }

    public ChatHistory GetSessionHistory(string sessionId)
    {
        string systemPrompt;
        lock (_kernelLock)
        {
            systemPrompt = _configService.Current.AI.SystemPrompt;
        }
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(systemPrompt));
        return state.History;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string sessionId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Kernel kernel;
        IChatCompletionService chat;
        string systemPrompt;

        lock (_kernelLock)
        {
            kernel = _kernel;
            chat = _chat;
            systemPrompt = _configService.Current.AI.SystemPrompt;
        }

        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(systemPrompt));
        state.Touch();

        // SessionContext 已在 Program.HandleChatStream 中按当前请求设置，供 Filter/Plugin 使用
        try
        {
            // Inject mode-specific instructions if needed
            string finalMessage = userMessage;
            if (state.Mode == "workspace" && !userMessage.Contains("[系统提示]"))
            {
                finalMessage = "[系统提示：当前处于 Workspace 模式，请尽可能使用 Markdown 表格、Mermaid.js (```mermaid) 或 <html_canvas> 来展示复杂的数据、流程图和逻辑结构。]\n" + userMessage;
            }
            else if (state.Mode == "assistant" && !userMessage.Contains("[系统提示]"))
            {
                finalMessage = "[系统提示：当前处于 Assistant 模式，请简明扼要地回答，主要基于提供的网页上下文。重要：如果用户要求高亮文字或在网页上添加笔记，你必须调用 BrowserPlugin 里的 highlight_webpage_text 或 add_floating_note 工具，千万不要只是口头回答。当工具返回的结果中包含「成功」时，请直接简短告知用户操作已成功完成，不要道歉或说无法使用。]\n" + userMessage;
            }

            state.History.AddUserMessage(finalMessage);
            TrimHistory(state.History);

            var execSettings = new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };

            var fullResponse = new System.Text.StringBuilder();

            await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(
                state.History, execSettings, kernel, ct))
            {
                if (chunk.Content is { Length: > 0 } text)
                {
                    fullResponse.Append(text);
                    yield return text;
                }
            }

            state.History.AddAssistantMessage(fullResponse.ToString());
            _logger.LogInformation("[{SessionId}] Turn completed, history={Turns} turns",
                sessionId, state.History.Count);
        }
        finally { }
    }

    private void TrimHistory(ChatHistory history)
    {
        // system(1) + user/assistant pairs, each pair = 2 messages
        var maxMessages = 1 + _maxTurns * 2;
        while (history.Count > maxMessages)
        {
            history.RemoveAt(1); // keep system prompt at index 0
        }
    }

    private void CleanupExpiredSessions(object? _)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_timeoutMinutes);
        var expired = _sessions.Where(kv => kv.Value.LastActivity < cutoff).Select(kv => kv.Key).ToList();

        foreach (var id in expired)
        {
            _sessions.TryRemove(id, out SessionState? _);
            _logger.LogInformation("Session {SessionId} expired and removed", id);
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();

    private sealed class SessionState
    {
        public ChatHistory History { get; }
        public DateTime LastActivity { get; private set; }
        public string Mode { get; set; } = "workspace";

        public SessionState(string systemPrompt)
        {
            History = new ChatHistory(systemPrompt);
            Touch();
        }

        public void Touch() => LastActivity = DateTime.UtcNow;
    }
}
