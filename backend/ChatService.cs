using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>当前选中的模型 Id，用于按 key 解析 IChatCompletionService。</summary>
    private string _activeModelId = "";
    private readonly int _maxTurns;
    private readonly int _timeoutMinutes;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly ILogger<ChatService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConfigService _configService;
    private readonly SkillService _skillService;
    private readonly McpClientManager _mcpManager;
    private readonly IToolSelector _toolSelector;
    private readonly IServiceProvider _serviceProvider;
    private readonly IKernelAccessor _kernelAccessor;
    private readonly object _kernelLock = new();

    public ChatService(IConfiguration config, ILogger<ChatService> logger, ILoggerFactory loggerFactory, ConfigService configService, SkillService skillService, McpClientManager mcpManager, IToolSelector toolSelector, IServiceProvider serviceProvider, IKernelAccessor kernelAccessor)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configService = configService;
        _skillService = skillService;
        _mcpManager = mcpManager;
        _toolSelector = toolSelector;
        _serviceProvider = serviceProvider;
        _kernelAccessor = kernelAccessor;

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

    /// <summary>将技能 Id（如 "Excel / XLSX"）规范为工具可用的函数名（如 "Excel_XLSX"），避免空格和斜杠导致协议不兼容。</summary>
    private static string SanitizeSkillFunctionName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Skill";
        var s = id.Trim().Replace("-", "_").Replace("/", "_").Replace(" ", "_");
        var sb = new System.Text.StringBuilder(s.Length);
        var prevUnderscore = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
                prevUnderscore = false;
            }
            else if (!prevUnderscore)
            {
                sb.Append('_');
                prevUnderscore = true;
            }
        }
        var result = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "Skill" : result;
    }

    /// <summary>获取要注册的模型列表：AiModels 中 Enabled 的项，或从 AI 迁移出的一条默认。</summary>
    private IReadOnlyList<AiModelEntry> GetModelEntriesToRegister()
    {
        var config = _configService.Current;
        if (config.AiModels != null && config.AiModels.Count > 0)
        {
            return config.AiModels.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Id)).ToList();
        }
        var ai = config.AI;
        return new List<AiModelEntry>
        {
            new AiModelEntry
            {
                Id = "default",
                DisplayName = "默认模型",
                Provider = ai?.Provider ?? "OpenAI",
                Endpoint = ai?.Endpoint ?? "",
                ApiKey = ai?.ApiKey ?? "",
                ModelId = ai?.ModelId ?? "gpt-4o-mini",
                SystemPrompt = ai?.SystemPrompt ?? "",
                Enabled = true
            }
        };
    }

    /// <summary>获取当前应使用的模型配置（用于 system prompt、日志）。</summary>
    private AiModelEntry? GetActiveModelEntry()
    {
        var config = _configService.Current;
        var activeId = (config.ActiveModelId ?? "").Trim();
        if (config.AiModels != null && config.AiModels.Count > 0)
        {
            var entry = config.AiModels.FirstOrDefault(e => string.Equals(e.Id, activeId, StringComparison.OrdinalIgnoreCase));
            if (entry != null) return entry;
            return config.AiModels.FirstOrDefault(e => e.Enabled) ?? config.AiModels.FirstOrDefault();
        }
        var ai = config.AI;
        return new AiModelEntry
        {
            Id = "default",
            DisplayName = "默认模型",
            Provider = ai?.Provider ?? "OpenAI",
            Endpoint = ai?.Endpoint ?? "",
            ApiKey = ai?.ApiKey ?? "",
            ModelId = ai?.ModelId ?? "gpt-4o-mini",
            SystemPrompt = ai?.SystemPrompt ?? "",
            Enabled = true
        };
    }

    private async Task RebuildKernelAsync()
    {
        var config = _configService.Current;
        var entries = GetModelEntriesToRegister();
        var builder = Kernel.CreateBuilder();
        var httpClient = new HttpClient(new OpenAiLoggingHandler(_loggerFactory.CreateLogger<OpenAiLoggingHandler>()));

        foreach (var entry in entries)
        {
            var modelId = (entry.ModelId ?? "").Trim();
            if (string.IsNullOrEmpty(modelId)) modelId = "gpt-4o-mini";
            var apiKey = (entry.ApiKey ?? "").Trim();
            var endpointTrimmed = (entry.Endpoint ?? "").Trim();

            Uri? endpointUri = null;
            if (endpointTrimmed.Length > 0
                && Uri.TryCreate(endpointTrimmed, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                endpointUri = parsed;
            }

            try
            {
                var provider = (entry.Provider ?? "OpenAI").Trim();
                if (string.IsNullOrEmpty(provider)) provider = "OpenAI";
                if (string.Equals(provider, "Azure", StringComparison.OrdinalIgnoreCase))
                {
                    var deployment = (entry.DeploymentName ?? "").Trim();
                    if (string.IsNullOrEmpty(deployment)) deployment = modelId;
                    modelId = deployment;
                }
                if (endpointUri != null)
                {
                    builder.AddOpenAIChatCompletion(
                        modelId: modelId,
                        apiKey: apiKey,
                        endpoint: endpointUri,
                        httpClient: httpClient,
                        serviceId: entry.Id);
                }
                else
                {
                    builder.AddOpenAIChatCompletion(
                        modelId: modelId,
                        apiKey: apiKey,
                        httpClient: httpClient,
                        serviceId: entry.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skip model {Id} ({Provider}): registration failed.", entry.Id, entry.Provider);
            }
        }

        var embeddedModel = _serviceProvider.GetRequiredService<IEmbeddedToolSelectionModel>();
        var embeddedChat = embeddedModel.GetChatCompletionService();
        if (embeddedChat != null)
            builder.Services.AddKeyedSingleton<IChatCompletionService>(EmbeddedToolSelectionModel.ServiceId, embeddedChat);

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

        // Tavily 原生插件：未停用时始终注册；Key 来自配置 tavilyApiKey、skillEnv 或环境变量（与 OpenClaw 的通用 skill env 思路一致）
        var tavilyApiKey = (_configService.Current.TavilyApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(tavilyApiKey) && _configService.Current.SkillEnv != null && _configService.Current.SkillEnv.TryGetValue("TAVILY_API_KEY", out var fromSkillEnv) && !string.IsNullOrEmpty(fromSkillEnv))
            tavilyApiKey = fromSkillEnv.Trim();
        if (string.IsNullOrEmpty(tavilyApiKey))
            tavilyApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
        if (!disabledBuiltIn.Contains("tavily"))
        {
            var tavilyLogger = _loggerFactory.CreateLogger<TavilyPlugin>();
            newKernel.Plugins.AddFromObject(new TavilyPlugin(tavilyApiKey, tavilyLogger), "Tavily");
        }

        var clawhubRunner = _serviceProvider.GetRequiredService<ClawhubScriptRunner>();
        if (!disabledBuiltIn.Contains("clawhub"))
            newKernel.Plugins.AddFromObject(new ClawhubSkillPlugin(_skillService, clawhubRunner, _configService, _loggerFactory.CreateLogger<ClawhubSkillPlugin>()), "ClawhubSkill");
        
        // 动态注册基于 Prompt 的 Skills（可执行技能且有原生适配器的如 tavily 不注册为 prompt，避免模型只拿到 SKILL.md 使用说明而非真正执行搜索）
        var userSkills = _skillService.GetAllSkills();
        var skillCount = 0;
        foreach (var skill in userSkills)
        {
            if (!skill.Enabled || string.IsNullOrWhiteSpace(skill.PromptTemplate)) continue;
            if (skill.IsExecutable && string.Equals(skill.Id, "tavily", StringComparison.OrdinalIgnoreCase))
                continue;
            
            try 
            {
                var safeName = SanitizeSkillFunctionName(skill.Id);
                var promptConfig = new PromptExecutionSettings { 
                    ExtensionData = new Dictionary<string, object> { 
                        { "max_tokens", 4000 },
                        { "temperature", 0.1 }
                    }
                };
                
                var function = newKernel.CreateFunctionFromPrompt(
                    skill.PromptTemplate,
                    promptConfig,
                    functionName: safeName,
                    description: skill.Description);
                    
                newKernel.Plugins.AddFromFunctions($"UserSkill_{safeName}", new[] { function });
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
        
        var activeEntry = GetActiveModelEntry();
        var activeId = config.AiModels != null && config.AiModels.Count > 0
            ? (config.ActiveModelId ?? "").Trim()
            : "default";
        if (string.IsNullOrEmpty(activeId) || entries.All(e => !string.Equals(e.Id, activeId, StringComparison.OrdinalIgnoreCase)))
            activeId = entries.Count > 0 ? entries[0].Id : "default";

        lock (_kernelLock)
        {
            _kernel = newKernel;
            _activeModelId = activeId;
            _kernelAccessor.Set(_kernel, _activeModelId);
        }

        _logger.LogInformation("Kernel rebuilt. ActiveModel: {ActiveId}, Plugins: {Count}, UserSkills: {SkillCount}, MCPs: {McpCount}",
            _activeModelId, _kernel.Plugins.Count, skillCount, mcpCount);
    }

    /// <summary>按当前选中的模型 Id 解析 IChatCompletionService，若无则回退到默认。</summary>
    private IChatCompletionService GetChatService(Kernel kernel)
    {
        if (string.IsNullOrEmpty(_activeModelId))
            return kernel.GetRequiredService<IChatCompletionService>();
        var keyed = kernel.Services.GetKeyedService<IChatCompletionService>(_activeModelId);
        return keyed ?? kernel.GetRequiredService<IChatCompletionService>();
    }

    private string GetActiveSystemPrompt()
    {
        var entry = GetActiveModelEntry();
        var prompt = entry?.SystemPrompt?.Trim();
        if (!string.IsNullOrEmpty(prompt)) return prompt;
        return _configService.Current.AI?.SystemPrompt ?? "";
    }

    public void SetSessionMode(string sessionId, string mode)
    {
        var systemPrompt = GetActiveSystemPrompt();
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(systemPrompt));
        state.Mode = mode;
        _logger.LogInformation("[{SessionId}] Mode updated to {Mode} in ChatService", sessionId, mode);
    }

    public ChatHistory GetSessionHistory(string sessionId)
    {
        var systemPrompt = GetActiveSystemPrompt();
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(systemPrompt));
        return state.History;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string sessionId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in StreamChatAsync(sessionId, userMessage, null, ct))
            yield return chunk;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<AttachmentDto>? attachments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Kernel kernel;
        IChatCompletionService chat;
        string systemPrompt;

        lock (_kernelLock)
        {
            kernel = _kernel;
            chat = GetChatService(kernel);
            systemPrompt = GetActiveSystemPrompt();
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

            if (attachments is { Count: > 0 })
            {
                var items = new ChatMessageContentItemCollection { new TextContent(finalMessage) };
                foreach (var att in attachments)
                {
                    if (string.IsNullOrWhiteSpace(att.Data)) continue;
                    var mime = string.IsNullOrWhiteSpace(att.MimeType) ? "image/png" : att.MimeType;
                    var dataUri = "data:" + mime + ";base64," + att.Data.Trim();
                    items.Add(new ImageContent(dataUri));
                }
                state.History.Add(new ChatMessageContent(AuthorRole.User, items));
            }
            else
            {
                state.History.AddUserMessage(finalMessage);
            }

            TrimHistory(state.History);

            var activeEntry = GetActiveModelEntry();
            var modelForLog = activeEntry?.ModelId ?? "(default)";
            var endpointForLog = "(default)";
            if (!string.IsNullOrWhiteSpace(activeEntry?.Endpoint) && Uri.TryCreate(activeEntry.Endpoint.Trim().Replace(" ", ""), UriKind.Absolute, out var ep) && (ep.Scheme == Uri.UriSchemeHttp || ep.Scheme == Uri.UriSchemeHttps))
                endpointForLog = ep.GetLeftPart(UriPartial.Authority);
            else if (!string.IsNullOrWhiteSpace(activeEntry?.Endpoint))
                endpointForLog = activeEntry.Endpoint.Trim().Length > 60 ? activeEntry.Endpoint.Trim()[..60] + "..." : activeEntry.Endpoint.Trim();
            var requestSummary = BuildChatHistorySummary(state.History);
            _logger.LogInformation(
                "[AI-REQUEST] SessionId={SessionId} Model={Model} Endpoint={Endpoint} MessageCount={Count} Messages={Messages}",
                sessionId, modelForLog, endpointForLog, state.History.Count, requestSummary);

            var aiConfig = _configService.Current.AI;
            var useTwoStage = aiConfig?.ToolSelectionTwoStage != false;
            IReadOnlyList<KernelFunction>? selectedFunctions = null;
            try
            {
                var recentHistory = state.History.Count > 1 ? state.History : null;
                if (useTwoStage)
                {
                    var selectedPairs = await _toolSelector.SelectFunctionsAsync(finalMessage, recentHistory, kernel, ct).ConfigureAwait(false);
                    if (selectedPairs is { Count: > 0 })
                        selectedFunctions = GetFunctionsByPluginAndFunctionNames(kernel, selectedPairs);
                }
                if (selectedFunctions == null || selectedFunctions.Count == 0)
                {
                    var availableNames = GetAvailablePluginNames(kernel);
                    var selectedNames = await _toolSelector.SelectPluginNamesAsync(finalMessage, recentHistory, availableNames, ct).ConfigureAwait(false);
                    selectedFunctions = selectedNames is { Count: > 0 }
                        ? GetFunctionsForPluginNames(kernel, selectedNames)
                        : null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{SessionId}] Tool selection failed, using all tools.", sessionId);
                selectedFunctions = null;
            }

            OpenAIPromptExecutionSettings execSettings;
            if (selectedFunctions is { Count: > 0 })
            {
                execSettings = new OpenAIPromptExecutionSettings
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(selectedFunctions)
                };
                _logger.LogInformation("[{SessionId}] Tool selection: {FunctionCount} functions",
                    sessionId, selectedFunctions.Count);
            }
            else
            {
                execSettings = new OpenAIPromptExecutionSettings
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                };
            }

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

    private static string BuildChatHistorySummary(ChatHistory history)
    {
        if (history == null || history.Count == 0) return "[]";
        const int previewLen = 400;
        var parts = new List<string>();
        for (var i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            var role = msg.Role.Label ?? msg.Role.ToString();
            var content = msg.Content ?? "";
            var len = content.Length;
            var preview = len <= previewLen ? content : content.AsSpan(0, previewLen).ToString() + "...";
            preview = preview.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
            if (preview.Length > 300) preview = preview.AsSpan(0, 300).ToString() + "...";
            parts.Add($"{{role:\"{role}\",contentLen:{len},preview:\"{preview}\"}}");
        }
        return "[" + string.Join(",", parts) + "]";
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

    /// <summary>获取当前 Kernel 中已注册的插件名列表。</summary>
    private static IReadOnlyList<string> GetAvailablePluginNames(Kernel kernel)
    {
        var list = new List<string>();
        foreach (var plugin in kernel.Plugins)
            list.Add(plugin.Name);
        return list;
    }

    /// <summary>从 Kernel 中取出指定插件名集合对应的所有 KernelFunction，用于 FunctionChoiceBehavior.Auto(functions)。</summary>
    private static IReadOnlyList<KernelFunction> GetFunctionsForPluginNames(Kernel kernel, IReadOnlyList<string> pluginNames)
    {
        if (pluginNames == null || pluginNames.Count == 0) return Array.Empty<KernelFunction>();
        var nameSet = new HashSet<string>(pluginNames, StringComparer.OrdinalIgnoreCase);
        var functions = new List<KernelFunction>();
        foreach (var plugin in kernel.Plugins)
        {
            if (!nameSet.Contains(plugin.Name)) continue;
            foreach (KernelFunction func in plugin)
                functions.Add(func);
        }
        return functions;
    }

    /// <summary>按 (插件名, 函数名) 列表从 Kernel 中取出对应 KernelFunction；用于两阶段工具选择结果。</summary>
    private static IReadOnlyList<KernelFunction> GetFunctionsByPluginAndFunctionNames(Kernel kernel, IReadOnlyList<(string PluginName, string FunctionName)> selected)
    {
        if (selected == null || selected.Count == 0) return Array.Empty<KernelFunction>();
        var functions = new List<KernelFunction>();
        foreach (var (pluginName, functionName) in selected)
        {
            if (kernel.Plugins.TryGetFunction(pluginName, functionName, out var func) && func != null)
                functions.Add(func);
        }
        return functions;
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
