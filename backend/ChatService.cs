using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Embeddings;
using OfficeCopilot.Server.Filters;
using OfficeCopilot.Server.Plugins;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Memory;
using OfficeCopilot.Server.Services.Plan;
using OfficeCopilot.Server.Services.CrossAgentTask;
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
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly IPlanStore _planStore;
    private readonly object _kernelLock = new();

    public ChatService(IConfiguration config, ILogger<ChatService> logger, ILoggerFactory loggerFactory, ConfigService configService, SkillService skillService, McpClientManager mcpManager, IToolSelector toolSelector, IServiceProvider serviceProvider, IKernelAccessor kernelAccessor, EmbeddingProvider embeddingProvider, IPlanStore planStore)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configService = configService;
        _skillService = skillService;
        _mcpManager = mcpManager;
        _toolSelector = toolSelector;
        _serviceProvider = serviceProvider;
        _kernelAccessor = kernelAccessor;
        _embeddingProvider = embeddingProvider;
        _planStore = planStore;

        var session = configService.Current.Session ?? new SessionConfig();
        _maxTurns = session.MaxHistoryTurns;
        _timeoutMinutes = session.TimeoutMinutes;
        var cleanupInterval = session.CleanupIntervalMinutes;

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

        // 阶段 3：嵌入服务（仅当配置了远程 Embedding 时注册；使用独立配置，不从大模型列表选）
        var embSrc = (config.EmbeddingSource ?? "").Trim();
        if (string.Equals(embSrc, "Remote", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(config.EmbeddingModelId))
        {
            var embModelId = (config.EmbeddingModelId ?? "").Trim();
            var embApiKey = (config.EmbeddingApiKey ?? "").Trim();
            var embEndpoint = (config.EmbeddingEndpoint ?? "").Trim();
            Uri? embUri = null;
            if (embEndpoint.Length > 0 && Uri.TryCreate(embEndpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                embUri = u;
#pragma warning disable CS0618 // AddOpenAITextEmbeddingGeneration 在 SK 1.72 中过时，仍可用
            try
            {
                if (embUri != null)
                    builder.AddOpenAITextEmbeddingGeneration(embModelId, embApiKey, embUri.ToString(), "Embedding", httpClient);
                else
                    builder.AddOpenAITextEmbeddingGeneration(embModelId, embApiKey, (string?)null, "Embedding", httpClient);
            }
#pragma warning restore CS0618
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skip embedding registration (Remote): {Message}", ex.Message);
            }
        }

        var newKernel = builder.Build();

        // 阶段 3：将当前 Kernel 的嵌入服务同步到 EmbeddingProvider，供记忆/ RAG 使用
#pragma warning disable CS0618
        var embeddingSvc = newKernel.Services.GetKeyedService<ITextEmbeddingGenerationService>("Embedding")
            ?? newKernel.Services.GetService<ITextEmbeddingGenerationService>();
#pragma warning restore CS0618
        _embeddingProvider.SetService(embeddingSvc);

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

        if (!disabledBuiltIn.Contains("currentdocument"))
        {
            var currentDocLogger = _loggerFactory.CreateLogger<CurrentDocumentPlugin>();
            newKernel.Plugins.AddFromObject(new CurrentDocumentPlugin(sessionManager, rpcManager, currentDocLogger), "CurrentDocument");
        }

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

        // 阶段 3：记忆插件（仅当已配置 Embedding 时注册）
        if (_embeddingProvider.IsConfigured && !disabledBuiltIn.Contains("memory"))
        {
            var memorySvc = _serviceProvider.GetRequiredService<IMemoryStoreService>();
            newKernel.Plugins.AddFromObject(new MemoryPlugin(memorySvc, _loggerFactory.CreateLogger<MemoryPlugin>()), "Memory");
        }

        if (!disabledBuiltIn.Contains("crossagenttask"))
        {
            var taskStore = _serviceProvider.GetRequiredService<ICrossAgentTaskStore>();
            newKernel.Plugins.AddFromObject(new CrossAgentTaskPlugin(taskStore, sessionManager, _loggerFactory.CreateLogger<CrossAgentTaskPlugin>()), "CrossAgentTask");
        }

        if (!disabledBuiltIn.Contains("plan"))
        {
            var planPlugin = _serviceProvider.GetRequiredService<PlanPlugin>();
            newKernel.Plugins.AddFromObject(planPlugin, "Plan");
        }

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
            IReadOnlyDictionary<string, string>? envOverlay = null;
            if (string.Equals(mcpConfig.Id, "accurate-data-mcp", StringComparison.OrdinalIgnoreCase))
            {
                var dir = (_configService.Current.AccurateDataDirectory ?? "").Trim();
                if (string.IsNullOrEmpty(dir))
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    dir = Path.Combine(appData, "OfficeCopilot", "AccurateData");
                }
                else
                {
                    dir = Environment.ExpandEnvironmentVariables(dir);
                    if (!Path.IsPathRooted(dir))
                        dir = Path.Combine(AppContext.BaseDirectory, dir);
                }
                envOverlay = new Dictionary<string, string> { ["ACCURATE_DATA_DIRECTORY"] = Path.GetFullPath(dir) };
            }
            if (string.Equals(mcpConfig.Id, "scheduled-task-mcp", StringComparison.OrdinalIgnoreCase))
            {
                var dir = (_configService.Current.ScheduledTasksDirectory ?? "").Trim();
                if (string.IsNullOrEmpty(dir))
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    dir = Path.Combine(appData, "OfficeCopilot", "ScheduledTasks");
                }
                else
                {
                    dir = Environment.ExpandEnvironmentVariables(dir);
                    if (!Path.IsPathRooted(dir))
                        dir = Path.Combine(AppContext.BaseDirectory, dir);
                }
                envOverlay = new Dictionary<string, string> { ["SCHEDULED_TASKS_DIRECTORY"] = Path.GetFullPath(dir) };
            }
            try
            {
                var client = await _mcpManager.StartClientAsync(mcpConfig, envOverlay);
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
        await foreach (var chunk in StreamChatAsync(sessionId, userMessage, null, null, null, null, null, ct))
            yield return chunk;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<AttachmentDto>? attachments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in StreamChatAsync(sessionId, userMessage, attachments, null, null, null, null, ct))
            yield return chunk;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<AttachmentDto>? attachments,
        string? knowledgeBaseId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in StreamChatAsync(sessionId, userMessage, attachments, knowledgeBaseId, null, null, null, ct))
            yield return chunk;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<AttachmentDto>? attachments,
        string? knowledgeBaseId,
        string? mode,
        string? planId,
        int? planCurrentStepIndex = null,
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
            if (attachments is { Count: > 0 })
            {
                var items = new ChatMessageContentItemCollection { new TextContent(userMessage) };
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
                state.History.AddUserMessage(userMessage);
            }

            TrimHistory(state.History);

            // 摘要压缩：当历史 token 接近预算且启用时，将最旧若干轮压缩为一段摘要
            var ctxForSummary = _configService.Current.ContextWindow ?? new ContextWindowConfig();
            if (ctxForSummary.SummarizationEnabled && state.History.Count > 5)
            {
                var budget = GetEffectiveMaxContextTokens() - ctxForSummary.ReservedSystemTokens - ctxForSummary.ReservedToolsTokens - ctxForSummary.ReservedOutputTokens;
                if (budget > 0)
                {
                    var totalTokens = 0;
                    for (var i = 0; i < state.History.Count; i++)
                        totalTokens += TokenEstimator.EstimateTokens(state.History[i].Content ?? "", ctxForSummary);
                    if (totalTokens >= (int)(budget * ctxForSummary.SummarizationTriggerRatio))
                    {
                        try
                        {
                            await TrySummarizeOldTurnsAsync(state.History, kernel, chat, ctxForSummary, sessionId, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[{SessionId}] Summarization failed, continuing without.", sessionId);
                        }
                    }
                }
            }

            // 阶段 3：长期记忆自动注入（本 session 优先 + 共享区 top-K，带来源标记；条数与总长走配置）
            var memorySvc = _serviceProvider.GetService<IMemoryStoreService>();
            if (memorySvc?.IsAvailable == true && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
            {
                try
                {
                    var sessionTopK = Math.Clamp(ctxForSummary.MemorySessionTopK, 1, 20);
                    var sharedTopK = Math.Clamp(ctxForSummary.MemorySharedTopK, 1, 20);
                    var sessionResults = await memorySvc.SearchAsync(userMessage, sessionTopK, sessionId, ct).ConfigureAwait(false);
                    var sharedResults = await memorySvc.SearchSharedAsync(userMessage, sharedTopK, ct).ConfigureAwait(false);
                    if (sessionResults.Count > 0 || sharedResults.Count > 0)
                    {
                        var parts = new List<string>();
                        if (sessionResults.Count > 0)
                            parts.Add("[以下是与当前对话相关的长期记忆，供参考]\n[本会话记忆]\n" + string.Join("\n", sessionResults.Select(r => "- " + r.Text)));
                        if (sharedResults.Count > 0)
                            parts.Add("[来自共享记忆]\n" + string.Join("\n", sharedResults.Select(r => "- " + r.Text)));
                        var memoryBlock = string.Join("\n\n", parts);
                        if (ctxForSummary.MemoryInjectionMaxChars > 0 && memoryBlock.Length > ctxForSummary.MemoryInjectionMaxChars)
                            memoryBlock = memoryBlock.AsSpan(0, ctxForSummary.MemoryInjectionMaxChars).ToString() + "\n（前文已截断）";
                        var currentSystem = state.History[0].Content ?? "";
                        state.History.RemoveAt(0);
                        state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem + "\n\n" + memoryBlock));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{SessionId}] Memory search failed, continuing without injection.", sessionId);
                }
            }

            // 阶段 3：知识库 RAG 注入（当请求带 knowledgeBaseId 时）
            if (!string.IsNullOrWhiteSpace(knowledgeBaseId) && memorySvc?.IsAvailable == true && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
            {
                try
                {
                    var kbResults = await memorySvc.SearchKnowledgeBaseAsync(knowledgeBaseId!.Trim(), userMessage, 5, ct).ConfigureAwait(false);
                    if (kbResults.Count > 0)
                    {
                        var kbLines = kbResults.Select(r => $"- {r.Text}").ToList();
                        var kbBlock = "[以下来自知识库的参考内容]\n" + string.Join("\n", kbLines);
                        var currentSystem = state.History[0].Content ?? "";
                        state.History.RemoveAt(0);
                        state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem + "\n\n" + kbBlock));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{SessionId}] Knowledge base search failed for {KbId}.", sessionId, knowledgeBaseId);
                }
            }

            // 跨 Agent 待办注入：拉取发给本端的任务，注入到 system
            var taskStore = _serviceProvider.GetService<ICrossAgentTaskStore>();
            var sessionManagerForTask = _serviceProvider.GetService<SessionManager>();
            if (taskStore != null && sessionManagerForTask != null && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
            {
                try
                {
                    var clientTypeForTask = sessionManagerForTask.GetClientType(sessionId);
                    var pending = await taskStore.GetPendingForTargetAsync(clientTypeForTask, sessionId, ct).ConfigureAwait(false);
                    if (pending.Count > 0)
                    {
                        var taskLines = pending.Select(t => $"- [id={t.Id}] {t.Description}").ToList();
                        var taskBlock = "[以下来自其他端的待办，请在本轮完成并调用 complete_cross_agent_task 标记完成]\n" + string.Join("\n", taskLines);
                        var currentSystem = state.History[0].Content ?? "";
                        state.History.RemoveAt(0);
                        state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem + "\n\n" + taskBlock));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{SessionId}] Cross-agent task pull failed.", sessionId);
                }
            }

            // 计划模式 / 计划注入
            var isPlanMode = string.Equals(mode?.Trim(), "plan", StringComparison.OrdinalIgnoreCase);
            (string Content, PlanMeta Meta)? planResult = null;
            if (state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
            {
                var currentSystem = state.History[0].Content ?? "";
                var systemModified = false;
                if (isPlanMode)
                {
                    currentSystem += "\n\n[当前为计划模式] 请根据用户描述仅生成实现计划（Markdown），并调用 create_plan 工具保存。不要执行具体操作，不要调用其他工具。";
                    systemModified = true;
                }
                if (!string.IsNullOrWhiteSpace(planId) && !isPlanMode)
                {
                    planResult = await _planStore.GetAsync(planId.Trim(), ct).ConfigureAwait(false);
                    if (planResult != null)
                    {
                        var planContent = planResult.Value.Content;
                        var stepIndex = planCurrentStepIndex is > 0 ? planCurrentStepIndex.Value : 1;
                        var stepOnly = PlanStepParser.GetStepAt(planContent, stepIndex);
                        if (!string.IsNullOrWhiteSpace(stepOnly))
                        {
                            currentSystem += "\n\n[当前绑定的计划·第 " + stepIndex + " 步]\n" + stepOnly;
                        }
                        else
                        {
                            var planMaxChars = (_configService.Current.ContextWindow ?? new ContextWindowConfig()).PlanContentMaxChars;
                            if (planMaxChars > 0 && planContent.Length > planMaxChars)
                                planContent = planContent.AsSpan(0, planMaxChars).ToString() + "\n（前文已截断）";
                            currentSystem += "\n\n[当前绑定的计划]\n" + planContent;
                        }
                        systemModified = true;
                    }
                }
                if (systemModified)
                {
                    state.History.RemoveAt(0);
                    state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem));
                }
            }

            var payloadChars = 0;
            for (var i = 0; i < state.History.Count; i++)
                payloadChars += (state.History[i].Content?.Length ?? 0);
            var phase = isPlanMode ? "plan" : "agent";
            _logger.LogInformation(
                "[AI-REQUEST] SessionId={SessionId} phase={Phase} turns={Turns} payloadChars={PayloadChars}",
                sessionId, phase, state.History.Count, payloadChars);

            var aiConfig = _configService.Current.AI;
            var useTwoStage = aiConfig?.ToolSelectionTwoStage != false;
            IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs = null;
            IReadOnlyList<string>? selectedNames = null;
            if (!isPlanMode)
            {
                try
                {
                    var recentHistory = state.History.Count > 1 ? state.History : null;
                    if (useTwoStage)
                    {
                        selectedPairs = await _toolSelector.SelectFunctionsAsync(userMessage, recentHistory, kernel, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var availableNames = GetAvailablePluginNames(kernel);
                        selectedNames = await _toolSelector.SelectPluginNamesAsync(userMessage, recentHistory, availableNames, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Tool selection failed, using all tools.", sessionId);
                    selectedPairs = null;
                    selectedNames = null;
                }
            }

            var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
            var clientType = sessionManager.GetClientType(sessionId);
            IReadOnlyList<KernelFunction>? selectedFunctions;
            if (isPlanMode)
            {
                selectedFunctions = GetPlanOnlyFunctions(kernel);
            }
            else
            {
                selectedFunctions = ResolveFunctionsByClientType(kernel, useTwoStage, selectedPairs, selectedNames, clientType);
                if (planResult != null && selectedFunctions != null)
                    selectedFunctions = MergePlanFunctions(kernel, selectedFunctions);
            }

            OpenAIPromptExecutionSettings execSettings;
            if (selectedFunctions is { Count: > 0 })
            {
                execSettings = new OpenAIPromptExecutionSettings
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(selectedFunctions)
                };
                _logger.LogDebug("[{SessionId}] Tool selection: clientType={ClientType} {FunctionCount} functions",
                    sessionId, clientType ?? "(null)", selectedFunctions.Count);
            }
            else
            {
                execSettings = new OpenAIPromptExecutionSettings
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                };
            }

            var fullResponse = new System.Text.StringBuilder();

            // 按 clientType 注入本端 Agent 身份说明（计划第四节约束）
            var identitySuffix = GetClientTypeIdentitySuffix(clientType);
            var historyToUse = state.History;
            if (!string.IsNullOrEmpty(identitySuffix) && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
            {
                var sysMsg = state.History[0];
                var newSystemText = (sysMsg.Content ?? "") + "\n\n" + identitySuffix;
                var newHistory = new ChatHistory(newSystemText);
                for (var i = 1; i < state.History.Count; i++)
                    newHistory.Add(state.History[i]);
                historyToUse = newHistory;
            }

            var collectedChunks = new List<string>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                collectedChunks.Clear();
                try
                {
                    await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(
                        historyToUse, execSettings, kernel, ct))
                    {
                        if (chunk.Content is { Length: > 0 } text)
                        {
                            fullResponse.Append(text);
                            collectedChunks.Add(text);
                        }
                    }
                    break;
                }
                catch (Exception ex) when (attempt == 0 && ctxForSummary.ContextLengthRetryEnabled && IsContextLengthError(ex))
                {
                    _logger.LogWarning(ex, "[{SessionId}] Context length exceeded, retrying with at most {MaxTurns} turns.", sessionId, ctxForSummary.ContextLengthRetryMaxTurns);
                    TrimHistoryForRetry(state.History, ctxForSummary.ContextLengthRetryMaxTurns);
                    historyToUse = state.History;
                    if (!string.IsNullOrEmpty(identitySuffix) && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
                    {
                        var sysMsg = state.History[0];
                        var newSystemText = (sysMsg.Content ?? "") + "\n\n" + identitySuffix;
                        var newHistory = new ChatHistory(newSystemText);
                        for (var i = 1; i < state.History.Count; i++)
                            newHistory.Add(state.History[i]);
                        historyToUse = newHistory;
                    }
                }
            }

            foreach (var text in collectedChunks)
                yield return text;

            state.History.AddAssistantMessage(fullResponse.ToString());
            _logger.LogInformation("[{SessionId}] Turn completed, turns={Turns}",
                sessionId, state.History.Count);
        }
        finally { }
    }

    /// <summary>将最旧若干轮（最多 6 轮）压缩为一段摘要并替换为一条消息。</summary>
    private async Task TrySummarizeOldTurnsAsync(ChatHistory history, Kernel kernel, IChatCompletionService chatService, ContextWindowConfig ctx, string sessionId, CancellationToken ct)
    {
        const int maxTurnsToSummarize = 6;
        var toTake = Math.Min(maxTurnsToSummarize * 2, history.Count - 1);
        if (toTake < 4)
            return;
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= toTake && i < history.Count; i++)
        {
            var msg = history[i];
            var role = msg.Role.Label ?? msg.Role.ToString() ?? "unknown";
            var content = msg.Content ?? "";
            sb.AppendLine($"[{role}] {content}");
        }
        var input = sb.ToString().Trim();
        if (input.Length == 0)
            return;
        var maxChars = Math.Max(100, Math.Min(ctx.SummarizationMaxSummaryChars, 2000));
        var systemPrompt = $"你是一个对话摘要助手。请将以下对话压缩为一段简短摘要，保留关键事实与结论。摘要不超过 {maxChars} 字。只输出摘要正文，不要输出「摘要：」等前缀。";
        var summaryHistory = new ChatHistory(systemPrompt);
        summaryHistory.AddUserMessage(input);
        var settings = new OpenAIPromptExecutionSettings { MaxTokens = 800, Temperature = 0.2f };
        var summaryBuilder = new System.Text.StringBuilder();
        await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(summaryHistory, settings, kernel, ct).ConfigureAwait(false))
        {
            if (chunk.Content is { Length: > 0 } text)
                summaryBuilder.Append(text);
        }
        var summary = summaryBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(summary))
            return;
        if (summary.Length > ctx.SummarizationMaxSummaryChars)
            summary = summary.AsSpan(0, ctx.SummarizationMaxSummaryChars).ToString() + "…";
        for (var i = 0; i < toTake; i++)
            history.RemoveAt(1);
        history.Insert(1, new ChatMessageContent(AuthorRole.User, "[此前对话摘要]\n" + summary));
        _logger.LogDebug("[{SessionId}] Summarized {Turns} turns into one block ({Len} chars).", sessionId, toTake / 2, summary.Length);
    }

    private static bool IsContextLengthError(Exception ex)
    {
        var msg = (ex.Message ?? "").ToLowerInvariant();
        return msg.Contains("context_length") || msg.Contains("maximum context") || msg.Contains("token limit")
            || msg.Contains("too many tokens");
    }

    /// <summary>为 context_length 重试裁剪历史：仅保留 system + 最近 maxTurns 轮。</summary>
    private static void TrimHistoryForRetry(ChatHistory history, int maxTurns)
    {
        var keepMessages = 1 + Math.Max(0, maxTurns) * 2;
        while (history.Count > keepMessages)
            history.RemoveAt(1);
    }

    /// <summary>获取当前生效的上下文 token 上限（优先使用当前模型的 ContextLength，否则用全局 ContextWindow.MaxContextTokens）。</summary>
    private int GetEffectiveMaxContextTokens()
    {
        var entry = GetActiveModelEntry();
        if (entry?.ContextLength is > 0)
            return entry.ContextLength.Value;
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        return ctx.MaxContextTokens > 0 ? ctx.MaxContextTokens : 64_000;
    }

    private void TrimHistory(ChatHistory history)
    {
        var session = _configService.Current.Session ?? new SessionConfig();
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        var maxMessagesByTurns = 1 + _maxTurns * 2;

        // 先按轮数上限裁（未配置 token 或未超 token 时也遵守轮数上限）
        while (history.Count > maxMessagesByTurns)
        {
            history.RemoveAt(1);
        }

        var maxContextTokens = GetEffectiveMaxContextTokens();
        if (maxContextTokens <= 0)
            return;

        var budget = maxContextTokens - ctx.ReservedOutputTokens;
        if (budget <= 0)
            return;

        int EstimateMessageTokens(ChatMessageContent msg)
        {
            var content = msg.Content ?? "";
            if (msg.Items is { Count: > 0 })
            {
                foreach (var item in msg.Items)
                {
                    if (item is Microsoft.SemanticKernel.TextContent text)
                        content += text.Text ?? "";
                }
            }
            return TokenEstimator.EstimateTokens(content, ctx);
        }

        var totalTokens = 0;
        for (var i = 0; i < history.Count; i++)
            totalTokens += EstimateMessageTokens(history[i]);

        var minMessagesToKeep = 1 + Math.Max(0, session.MinTurnsToKeep) * 2; // system + 至少 N 轮
        while (totalTokens > budget && history.Count > minMessagesToKeep)
        {
            if (history.Count <= 2)
                break;
            var removed = EstimateMessageTokens(history[1]) + (history.Count > 2 ? EstimateMessageTokens(history[2]) : 0);
            history.RemoveAt(1);
            if (history.Count > 1)
                history.RemoveAt(1);
            totalTokens -= removed;
        }
    }

    /// <summary>按 clientType 解析本轮暴露给模型的工具：过滤 selectedPairs/selectedNames，或使用该端全量允许的函数。Office/WPS 端在阶段二始终追加 current_run_document_script 作为保底工具（不参与阶段一子类选择）。</summary>
    private static IReadOnlyList<KernelFunction>? ResolveFunctionsByClientType(
        Kernel kernel,
        bool useTwoStage,
        IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs,
        IReadOnlyList<string>? selectedNames,
        string? clientType)
    {
        IReadOnlyList<KernelFunction>? result = null;
        if (useTwoStage)
        {
            if (selectedPairs is { Count: > 0 })
            {
                var filtered = ClientTypeToolFilter.Filter(selectedPairs, clientType);
                if (filtered.Count > 0)
                    result = GetFunctionsByPluginAndFunctionNames(kernel, filtered);
            }
            if (result == null)
                result = ClientTypeToolFilter.GetAllowedFunctions(kernel, clientType);
        }
        else
        {
            if (selectedNames is { Count: > 0 })
            {
                var pairs = new List<(string PluginName, string FunctionName)>();
                foreach (var plugin in kernel.Plugins)
                {
                    if (!selectedNames.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    foreach (KernelFunction func in plugin)
                        pairs.Add((plugin.Name, func.Name));
                }
                var filtered = ClientTypeToolFilter.Filter(pairs, clientType);
                if (filtered.Count > 0)
                    result = GetFunctionsByPluginAndFunctionNames(kernel, filtered);
            }
            if (result == null)
                result = ClientTypeToolFilter.GetAllowedFunctions(kernel, clientType);
        }

        // Office/WPS 端：阶段二始终追加 current_run_document_script 作为保底工具，不参与阶段一子类选择
        if (result != null && IsOfficeOrWpsClient(clientType) &&
            kernel.Plugins.TryGetFunction("CurrentDocument", "current_run_document_script", out var fallback) && fallback != null)
        {
            if (!result.Any(f => string.Equals(f.Name, "current_run_document_script", StringComparison.OrdinalIgnoreCase)))
            {
                var withFallback = new List<KernelFunction>(result) { fallback };
                return withFallback;
            }
        }
        return result;
    }

    /// <summary>按 clientType 返回本端 Agent 身份说明，用于追加到 system 提示（计划第四节：每端身份写清）。</summary>
    private static string GetClientTypeIdentitySuffix(string? clientType)
    {
        var ct = (clientType ?? "").Trim();
        if (string.IsNullOrEmpty(ct)) return "";
        if (string.Equals(ct, "chrome", StringComparison.OrdinalIgnoreCase))
            return "你是浏览器侧助手，负责当前页面的标注、笔记、截图等；文档编辑请由用户在 Word/Excel 任务窗格端完成。";
        if (string.Equals(ct, "office-word", StringComparison.OrdinalIgnoreCase))
            return "你是 Word 侧助手，负责当前打开的 Word 文档；网页相关操作请由用户在浏览器侧边栏端完成。";
        if (string.Equals(ct, "office-excel", StringComparison.OrdinalIgnoreCase))
            return "你是 Excel 侧助手，负责当前打开的 Excel 工作簿；网页相关操作请由用户在浏览器侧边栏端完成。";
        if (string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase))
            return "你是 WPS 侧助手，负责当前打开的 WPS 文档；网页相关操作请由用户在浏览器侧边栏端完成。";
        return "";
    }

    private static bool IsOfficeOrWpsClient(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)) return false;
        var ct = clientType.Trim();
        return string.Equals(ct, "office-word", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "office-excel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>计划模式下仅暴露 Plan 插件的 create_plan、get_plan。</summary>
    private static IReadOnlyList<KernelFunction>? GetPlanOnlyFunctions(Kernel kernel)
    {
        var list = new List<KernelFunction>();
        foreach (var plugin in kernel.Plugins)
        {
            if (!string.Equals(plugin.Name, "Plan", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (KernelFunction func in plugin)
            {
                if (string.Equals(func.Name, "create_plan", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(func.Name, "get_plan", StringComparison.OrdinalIgnoreCase))
                    list.Add(func);
            }
            break;
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>将 Plan 插件的 get_plan、update_plan、execute_plan_step 并入已选函数列表（供按计划执行时使用）。</summary>
    private static IReadOnlyList<KernelFunction> MergePlanFunctions(Kernel kernel, IReadOnlyList<KernelFunction> existing)
    {
        var set = new HashSet<string>(existing.Select(f => f.PluginName + "." + f.Name), StringComparer.OrdinalIgnoreCase);
        var list = new List<KernelFunction>(existing);
        foreach (var plugin in kernel.Plugins)
        {
            if (!string.Equals(plugin.Name, "Plan", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (KernelFunction func in plugin)
            {
                if (string.Equals(func.Name, "get_plan", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(func.Name, "update_plan", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(func.Name, "execute_plan_step", StringComparison.OrdinalIgnoreCase))
                {
                    var key = plugin.Name + "." + func.Name;
                    if (!set.Contains(key)) { set.Add(key); list.Add(func); }
                }
            }
            break;
        }
        return list;
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

        public SessionState(string systemPrompt)
        {
            History = new ChatHistory(systemPrompt);
            Touch();
        }

        public void Touch() => LastActivity = DateTime.UtcNow;
    }
}
