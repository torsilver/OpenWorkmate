using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
using OfficeCopilot.Server.Services.ScheduledTask;
using OfficeCopilot.Server.Mcp;

namespace OfficeCopilot.Server;

public sealed class ChatService : IDisposable
{
    private Kernel _kernel = null!;
    /// <summary>当前选中的模型 Id，用于按 key 解析 IChatCompletionService。</summary>
    private string _activeModelId = "";
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly ILogger<ChatService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConfigService _configService;
    private readonly SkillService _skillService;
    private readonly McpClientManager _mcpManager;
    private readonly IToolSelector _toolSelector;
    private readonly IToolIndexService _toolIndex;
    private readonly IVectorStore _vectorStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly IKernelAccessor _kernelAccessor;
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly IPlanStore _planStore;
    private readonly AgentDebugStatsService _agentDebugStats;
    private readonly object _kernelLock = new();

    public ChatService(IConfiguration config, ILogger<ChatService> logger, ILoggerFactory loggerFactory, ConfigService configService, SkillService skillService, McpClientManager mcpManager, IToolSelector toolSelector, IToolIndexService toolIndex, IVectorStore vectorStore, IServiceProvider serviceProvider, IKernelAccessor kernelAccessor, EmbeddingProvider embeddingProvider, IPlanStore planStore, AgentDebugStatsService agentDebugStats)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configService = configService;
        _skillService = skillService;
        _mcpManager = mcpManager;
        _toolSelector = toolSelector;
        _toolIndex = toolIndex;
        _vectorStore = vectorStore;
        _serviceProvider = serviceProvider;
        _kernelAccessor = kernelAccessor;
        _embeddingProvider = embeddingProvider;
        _planStore = planStore;
        _agentDebugStats = agentDebugStats;

        var session = configService.Current.Session ?? new SessionConfig();
        var cleanupInterval = session.CleanupIntervalMinutes;

        RebuildKernelAsync(skipUserToolIndexSync: true).GetAwaiter().GetResult();
        _configService.OnConfigChanged += () => _ = RebuildKernelAsync(skipUserToolIndexSync: false);
        _skillService.OnSkillsChanged += () => _ = RebuildKernelAsync(skipUserToolIndexSync: false);

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

    /// <summary>重建 Kernel（内置插件 + 用户 Skill + MCP）；可由 Program 在 --build-tool-index 模式下调用。</summary>
    /// <param name="skipUserToolIndexSync">为 true 时不调度用户工具向量增量同步（用于进程首次启动与预构建中间步骤）。</param>
    public async Task RebuildKernelAsync(bool skipUserToolIndexSync = false)
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

        // 阶段 3：嵌入服务（使用当前选中的 Embedding 模型条目；未配置或未选中则不注册）
        var activeEmb = _configService.GetActiveEmbeddingEntry();
        if (activeEmb != null && string.Equals((activeEmb.Source ?? "").Trim(), "Remote", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(activeEmb.ModelId))
        {
            var embModelId = (activeEmb.ModelId ?? "").Trim();
            var embApiKey = (activeEmb.ApiKey ?? "").Trim();
            var embEndpoint = (activeEmb.Endpoint ?? "").Trim();
            Uri? embUri = null;
            if (embEndpoint.Length > 0 && Uri.TryCreate(embEndpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                embUri = u;
#pragma warning disable CS0618 // AddOpenAITextEmbeddingGeneration 在 SK 1.72 中过时，仍可用
            try
            {
                if (embUri != null)
                {
                    // SK 该重载第 3 参数为 orgId 非 endpoint，请求会发往 api.openai.com；用 Handler 将请求重写到配置的 endpoint
                    var redirectHandler = new EmbeddingEndpointRedirectHandler(embUri.ToString());
                    redirectHandler.InnerHandler = new OpenAiLoggingHandler(_loggerFactory.CreateLogger<OpenAiLoggingHandler>());
                    var embHttpClient = new HttpClient(redirectHandler);
                    builder.AddOpenAITextEmbeddingGeneration(embModelId, embApiKey, (string?)null, "Embedding", embHttpClient);
                    _logger.LogInformation("Embedding registered: Id={EmbId}, Endpoint={Endpoint}", activeEmb.Id, embUri.ToString());
                }
                else
                {
                    _logger.LogWarning("Embedding 未配置 Endpoint，已跳过注册（避免误用默认 OpenAI 地址）。请在设置中为该条填写 Endpoint 并保存。Id={EmbId}", activeEmb.Id);
                }
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
        var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
        var securityFilter = new SecurityFilter(_loggerFactory.CreateLogger<SecurityFilter>(), _configService, hitlManager, sessionManager);
        newKernel.FunctionInvocationFilters.Add(securityFilter);
        // 注入当前会话 ID，供 BrowserPlugin 等插件在工具调用时使用
        newKernel.FunctionInvocationFilters.Add(new SessionContextFilter(_loggerFactory.CreateLogger<SessionContextFilter>()));
        // 工具调用状态对前端可见：推送 tool_invocation_start / tool_invocation_end
        newKernel.FunctionInvocationFilters.Add(new ToolStatusFilter(sessionManager, _loggerFactory.CreateLogger<ToolStatusFilter>(), _agentDebugStats));

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
        if (!disabledBuiltIn.Contains("ppt"))
            newKernel.Plugins.AddFromObject(new PptPlugin(_loggerFactory.CreateLogger<PptPlugin>()), "Ppt");

        var rpcManager = _serviceProvider.GetRequiredService<RpcManager>();
        var screenshotCache = _serviceProvider.GetRequiredService<ScreenshotCacheService>();
        var attachmentCache = _serviceProvider.GetRequiredService<AttachmentCacheService>();
        var browserPluginLogger = _loggerFactory.CreateLogger<BrowserPlugin>();
        var filePluginLogger = _loggerFactory.CreateLogger<FilePlugin>();
        if (!disabledBuiltIn.Contains("browser"))
            newKernel.Plugins.AddFromObject(new BrowserPlugin(sessionManager, rpcManager, screenshotCache, browserPluginLogger), "Browser");
        if (!disabledBuiltIn.Contains("file"))
            newKernel.Plugins.AddFromObject(new FilePlugin(screenshotCache, attachmentCache, filePluginLogger), "File");
        if (!disabledBuiltIn.Contains("system"))
            newKernel.Plugins.AddFromObject(new SystemPlugin(), "System");
        if (!disabledBuiltIn.Contains("mcp_stt"))
        {
            var transcribeService = _serviceProvider.GetRequiredService<ITranscribeService>();
            var sttPluginLogger = _loggerFactory.CreateLogger<SttPlugin>();
            newKernel.Plugins.AddFromObject(new SttPlugin(transcribeService, sttPluginLogger), "MCP_STT");
        }
        if (!disabledBuiltIn.Contains("mcp_ocr"))
        {
            var ocrService = _serviceProvider.GetRequiredService<IOcrService>();
            var ocrPluginLogger = _loggerFactory.CreateLogger<OcrPlugin>();
            newKernel.Plugins.AddFromObject(new OcrPlugin(ocrService, ocrPluginLogger), "MCP_OCR");
        }

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
            newKernel.Plugins.AddFromObject(new MemoryPlugin(memorySvc, sessionManager, _loggerFactory.CreateLogger<MemoryPlugin>()), "Memory");
        }

        if (!disabledBuiltIn.Contains("context"))
            newKernel.Plugins.AddFromObject(new CompactConversationPlugin(this), "Context");

        if (!disabledBuiltIn.Contains("subagent"))
            newKernel.Plugins.AddFromObject(new SubagentPlugin(this), "Subagent");

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

        if (!disabledBuiltIn.Contains("skillauthor"))
        {
            var skillAuthorPlugin = _serviceProvider.GetRequiredService<SkillAuthorPlugin>();
            newKernel.Plugins.AddFromObject(skillAuthorPlugin, "SkillAuthor");
        }

        if (!disabledBuiltIn.Contains("user_options"))
        {
            var userOptionsManager = _serviceProvider.GetRequiredService<UserOptionsManager>();
            var userOptionsLogger = _loggerFactory.CreateLogger<UserOptionsPlugin>();
            newKernel.Plugins.AddFromObject(new UserOptionsPlugin(userOptionsManager, userOptionsLogger), "UserOptions");
        }

        if (!disabledBuiltIn.Contains("accuratedata"))
            newKernel.Plugins.AddFromObject(new AccurateDataPlugin(_configService), "AccurateData");

        if (!disabledBuiltIn.Contains("scheduledtask"))
        {
            var scheduledTaskStore = _serviceProvider.GetRequiredService<IScheduledTaskStore>();
            newKernel.Plugins.AddFromObject(new ScheduledTaskPlugin(scheduledTaskStore), "ScheduledTask");
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

        // 动态注册外部 MCP 服务（accurate-data、scheduled-task 已收编为内置插件，不再在此注入）
        var mcpCount = 0;
        foreach (var mcpConfig in _configService.Current.McpServers)
        {
            if (!mcpConfig.Enabled)
                continue;
            try
            {
                var client = await _mcpManager.StartClientAsync(mcpConfig, envOverlay: null);
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

        if (!skipUserToolIndexSync)
            _ = SyncUserToolIndexInBackgroundAsync(newKernel);
    }

    /// <summary>后台增量同步用户工具索引（配置/技能变更后）；不阻塞请求。</summary>
    private async Task SyncUserToolIndexInBackgroundAsync(Kernel kernel)
    {
        try
        {
            await _toolIndex.SyncUserToolIndexAsync(kernel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "User tool index sync failed.");
        }
    }

    /// <summary>按当前选中的模型 Id 解析 IChatCompletionService，若无则回退到默认。</summary>
    private IChatCompletionService GetChatService(Kernel kernel)
    {
        if (string.IsNullOrEmpty(_activeModelId))
            return kernel.GetRequiredService<IChatCompletionService>();
        var keyed = kernel.Services.GetKeyedService<IChatCompletionService>(_activeModelId);
        return keyed ?? kernel.GetRequiredService<IChatCompletionService>();
    }

    /// <summary>追加到主对话 system：Memory / AccurateData / Plan / UserOptions 分工与双触发（用户可点名 + 模型可按需启用）。</summary>
    private const string BuiltinTaskPluginSystemGuidance = """
[内置插件：记忆 / 准确数据 / 计划 / 候选项确认]
以下均为内置能力（非外接 MCP）。用户可在对话中明确要求；你也应在符合条件时主动选用对应工具。

- Memory：记录与检索用户的习惯、取向、偏好与长期关键事实；不用于存大块中间数据或任务步骤正文。
- AccurateData：多步复杂任务中按固定 id 精确读写大块结构化中间结果以减轻上下文；不替代语义记忆或计划步骤流。
- Plan：将复杂任务拆解为可保存、可按步执行的计划（Markdown 步骤）；不替代 AccurateData 存原始数据块，不替代 Memory 记偏好。
- UserOptions（工具 ask_options）：当任务存在多种合理解法、输出格式或分步选择且不宜替用户拍板时，用侧栏分步单选让用户确认；用 stepsJson 描述每步问题与选项，勿要求用户在聊天里回复「选 A/B」。

记忆、准确数据、计划与候选项确认可同时使用（例如：先 ask_options 定方案，再 Plan 执行并用 AccurateData 存中间结果）。
""";

    private string GetActiveSystemPrompt()
    {
        var entry = GetActiveModelEntry();
        var prompt = entry?.SystemPrompt?.Trim();
        var basePrompt = !string.IsNullOrEmpty(prompt)
            ? prompt
            : (_configService.Current.AI?.SystemPrompt ?? "").Trim();
        var guidance = BuiltinTaskPluginSystemGuidance.Trim();
        if (string.IsNullOrEmpty(basePrompt))
            return guidance;
        return basePrompt + "\n\n" + guidance;
    }

    public ChatHistory GetSessionHistory(string sessionId)
    {
        var systemPrompt = GetActiveSystemPrompt();
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(systemPrompt));
        return state.History;
    }

    public async IAsyncEnumerable<StreamItem> StreamChatAsync(
        string sessionId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in StreamChatAsync(sessionId, userMessage, null, null, null, null, null, null, ct))
            yield return item;
    }

    public async IAsyncEnumerable<StreamItem> StreamChatAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<AttachmentDto>? attachments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in StreamChatAsync(sessionId, userMessage, attachments, null, null, null, null, null, ct))
            yield return item;
    }

    public async IAsyncEnumerable<StreamItem> StreamChatAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<AttachmentDto>? attachments,
        string? knowledgeBaseId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in StreamChatAsync(sessionId, userMessage, attachments, knowledgeBaseId, null, null, null, null, ct))
            yield return item;
    }

    public async IAsyncEnumerable<StreamItem> StreamChatAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<AttachmentDto>? attachments,
        string? knowledgeBaseId,
        string? mode,
        string? planId,
        int? planCurrentStepIndex = null,
        IReadOnlyList<string>? attachmentRefs = null,
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
            if (attachmentRefs is { Count: > 0 })
            {
                // 仅将引用写入对话，不把 base64/ImageContent 放入上下文；模型通过工具（如 get_attachment_path、OCR）按 ref 在客户机处理
                var refList = string.Join(", ", attachmentRefs);
                var messageText = "用户附带了 [" + refList + "]。"
                    + (string.IsNullOrWhiteSpace(userMessage) ? "" : " 用户说：" + userMessage);
                state.History.AddUserMessage(messageText);
            }
            else if (attachments is { Count: > 0 })
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

            var sessionManagerForStatus = _serviceProvider.GetRequiredService<SessionManager>();
            await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在准备上下文…", ct).ConfigureAwait(false);

            var ctxConfig = _configService.Current.ContextWindow ?? new ContextWindowConfig();
            var historyBudget = GetEffectiveMaxContextTokens()
                - ctxConfig.ReservedSystemTokens
                - ctxConfig.ReservedToolsTokens
                - ctxConfig.ReservedOutputTokens;

            if (historyBudget > 0 && !ctxConfig.PassThroughContext)
            {
                var totalTokens = EstimateHistoryTokens(state.History, ctxConfig);

                // 摘要优先：先判断是否触发摘要（基于原始内容），避免截断后摘要质量下降
                var summarized = false;
                if (ctxConfig.SummarizationEnabled && state.History.Count > 5
                    && totalTokens >= (int)(historyBudget * ctxConfig.SummarizationTriggerRatio))
                {
                    try
                    {
                        await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在整理历史对话…", ct).ConfigureAwait(false);
                        var sumResult = await TrySummarizeOldTurnsAsync(state.History, kernel, chat, ctxConfig, sessionId, ct).ConfigureAwait(false);
                        totalTokens = EstimateHistoryTokens(state.History, ctxConfig);
                        summarized = sumResult.DidCompact;
                        if (sumResult.DidCompact)
                        {
                            var offloadDir = GetConversationHistoryDirectory(ctxConfig);
                            var offloadConfigured = !string.IsNullOrWhiteSpace(offloadDir);
                            var ctxTrace = AgentTraceFormatter.BuildContextSummarizationSuccessTrace(
                                sumResult.MessagesRemoved, sumResult.SummaryLength, ctxConfig, offloadConfigured);
                            await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context", ctxTrace.Title, ctxTrace.Detail, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[{SessionId}] Summarization failed, continuing without.", sessionId);
                        var failTrace = AgentTraceFormatter.BuildContextSummarizationFailureTrace(ErrorMessageHelper.GetFriendlyMessage(ex));
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context", failTrace.Title, failTrace.Detail, ct).ConfigureAwait(false);
                    }
                }

                // 仅在未触发摘要时执行截断，避免对已摘要内容重复处理
                if (!summarized && ctxConfig.TruncateToolArgsThresholdRatio > 0 && ctxConfig.TruncateToolArgsMaxChars > 0
                    && totalTokens >= (int)(historyBudget * ctxConfig.TruncateToolArgsThresholdRatio))
                {
                    var keep = Math.Max(0, ctxConfig.TruncateToolArgsKeepMessages);
                    var maxChars = Math.Max(100, ctxConfig.TruncateToolArgsMaxChars);
                    var truncateSuffix = "…(已截断)";
                    var oldEndIndex = Math.Max(0, state.History.Count - keep - 1);
                    var truncatedCount = 0;
                    for (var i = 1; i <= oldEndIndex && i < state.History.Count; i++)
                    {
                        var msg = state.History[i];
                        var content = msg.Content ?? "";
                        if (content.Length <= maxChars) continue;
                        var truncated = content.AsSpan(0, maxChars).ToString() + truncateSuffix;
                        state.History[i] = new ChatMessageContent(msg.Role, truncated);
                        truncatedCount++;
                    }
                    if (truncatedCount > 0)
                    {
                        var trTrace = AgentTraceFormatter.BuildContextTruncateTrace(
                            truncatedCount, keep, maxChars, ctxConfig.TruncateToolArgsThresholdRatio, totalTokens, historyBudget);
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "context", trTrace.Title, trTrace.Detail, ct).ConfigureAwait(false);
                    }
                }
            }

            // 阶段 3：长期记忆自动注入（本 session 优先 + 共享区 top-K，带来源标记；条数与总长走配置）
            var warnings = new List<string>();
            var memorySvc = _serviceProvider.GetService<IMemoryStoreService>();
            if (memorySvc?.IsAvailable == true && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
            {
                try
                {
                    await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在检索相关记忆…", ct).ConfigureAwait(false);
                    var sessionTopK = Math.Clamp(ctxConfig.MemorySessionTopK, 1, 20);
                    var sharedTopK = Math.Clamp(ctxConfig.MemorySharedTopK, 1, 20);
                    var sessionResults = await memorySvc.SearchAsync(userMessage, sessionTopK, sessionId, ct).ConfigureAwait(false);
                    var sharedResults = await memorySvc.SearchSharedAsync(userMessage, sharedTopK, ct).ConfigureAwait(false);
                    var memTrace = AgentTraceFormatter.BuildMemoryTrace(sessionResults, sharedResults, sessionTopK, sharedTopK);
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "memory", memTrace.Title, memTrace.Detail, ct).ConfigureAwait(false);
                    if (sessionResults.Count > 0 || sharedResults.Count > 0)
                    {
                        var parts = new List<string>();
                        if (sessionResults.Count > 0)
                            parts.Add("[以下是与当前对话相关的长期记忆，供参考]\n[本会话记忆]\n" + string.Join("\n", sessionResults.Select(r => "- " + r.Text)));
                        if (sharedResults.Count > 0)
                            parts.Add("[来自共享记忆]\n" + string.Join("\n", sharedResults.Select(r => "- " + r.Text)));
                        var memoryBlock = string.Join("\n\n", parts);
                        if (ctxConfig.MemoryInjectionMaxChars > 0 && memoryBlock.Length > ctxConfig.MemoryInjectionMaxChars)
                            memoryBlock = memoryBlock.AsSpan(0, ctxConfig.MemoryInjectionMaxChars).ToString() + "\n（前文已截断）";
                        var currentSystem = state.History[0].Content ?? "";
                        state.History.RemoveAt(0);
                        state.History.Insert(0, new ChatMessageContent(AuthorRole.System, currentSystem + "\n\n" + memoryBlock));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{SessionId}] Memory search failed, continuing without injection.", sessionId);
                    var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
                    warnings.Add("记忆检索失败：" + friendly + " 当前对话未注入长期记忆。");
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "memory", "长期记忆检索失败", friendly, ct).ConfigureAwait(false);
                }
            }

            // 阶段 3：知识库 RAG 注入（当请求带 knowledgeBaseId 时）
            if (!string.IsNullOrWhiteSpace(knowledgeBaseId) && memorySvc?.IsAvailable == true && state.History.Count > 0 && state.History[0].Role == AuthorRole.System)
            {
                try
                {
                    await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在检索知识库…", ct).ConfigureAwait(false);
                    var kbResults = await memorySvc.SearchKnowledgeBaseAsync(knowledgeBaseId!.Trim(), userMessage, 5, ct).ConfigureAwait(false);
                    var kbTrace = AgentTraceFormatter.BuildKnowledgeBaseTrace(knowledgeBaseId!.Trim(), kbResults);
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "knowledgeBase", kbTrace.Title, kbTrace.Detail, ct).ConfigureAwait(false);
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
                    var friendly = ErrorMessageHelper.GetFriendlyMessage(ex);
                    warnings.Add("知识库检索失败：" + friendly);
                    await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "knowledgeBase", "知识库检索失败", friendly, ct).ConfigureAwait(false);
                }
            }

            foreach (var w in warnings)
                yield return new StreamItem(IsWarning: true, Content: w);

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
                            var planMaxChars = ctxConfig.PlanContentMaxChars;
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
            var clientType = sessionManagerForStatus.GetClientType(sessionId);
            IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs = null;
            if (!isPlanMode)
            {
                await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在筛选可用工具…", ct).ConfigureAwait(false);
                _agentDebugStats.IncrementToolSelectionTotal();
                try
                {
                    var recentHistory = state.History.Count > 1 ? state.History : null;
                    var embeddingConfigured = _embeddingProvider.IsConfigured;
                    var storePersistent = _vectorStore.IsPersistent;
                    _logger.LogInformation("[{SessionId}] ToolSelection: entry clientType={ClientType} embeddingConfigured={Emb} storePersistent={Store}.",
                        sessionId, clientType ?? "(null)", embeddingConfigured, storePersistent);
                    if (embeddingConfigured && storePersistent)
                    {
                        var userPrompt = BuildToolSelectionUserPrompt(userMessage, recentHistory);
                        var vectorSearch = await _toolIndex.SearchToolsAsync(
                            userPrompt, clientType,
                            topK: ctxConfig.ToolSearchTopK,
                            minScore: ctxConfig.ToolSearchMinScore,
                            minCount: ctxConfig.ToolSearchMinCount,
                            ct).ConfigureAwait(false);
                        _logger.LogInformation("[{SessionId}] ToolSelection: vector search result count={Count} goodEnough={GoodEnough}.",
                            sessionId, vectorSearch.Results.Count, vectorSearch.GoodEnough);
                        var vectorFirstChosen = vectorSearch.GoodEnough && vectorSearch.Results.Count > 0;
                        var scored = vectorSearch.ScoredHits;
                        var maxS = scored.Count > 0 ? scored[0].Score : 0.0;
                        double? secondS = scored.Count >= 2 ? scored[1].Score : null;
                        _agentDebugStats.RecordVectorSearchCompleted(new VectorSearchTelemetry(
                            clientType,
                            maxS,
                            secondS,
                            scored.Count,
                            vectorSearch.GoodEnough,
                            vectorFirstChosen));
                        string vectorDecision;
                        if (vectorFirstChosen)
                        {
                            selectedPairs = MergeVectorResultsWithAlwaysInclude(vectorSearch.Results, aiConfig ?? new AiConfig(), kernel);
                            _logger.LogInformation("[{SessionId}] ToolSelection: using vector-first path, selectedPairsCount={Count}.", sessionId, selectedPairs.Count);
                            _logger.LogDebug("[{SessionId}] Tool selection: vector-first used, {Count} tools.", sessionId, selectedPairs.Count);
                            vectorDecision = "决策：已采用向量优先路径（合并 AlwaysInclude 后 (插件,函数) 对数=" + selectedPairs.Count + "）。";
                        }
                        else
                        {
                            vectorDecision = "决策：向量命中未达 goodEnough 或为空，将调用两阶段子类筛选。";
                        }
                        var vectorDetail = AgentTraceFormatter.BuildToolVectorSearchDetail(
                            clientType, vectorSearch,
                            ctxConfig.ToolSearchTopK, ctxConfig.ToolSearchMinScore, ctxConfig.ToolSearchMinCount,
                            vectorDecision);
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", "工具选择：向量索引检索", vectorDetail, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!embeddingConfigured)
                            _agentDebugStats.RecordVectorSkippedNoEmbedding();
                        else
                            _agentDebugStats.RecordVectorSkippedNonPersistent();
                        var skipDetail = AgentTraceFormatter.BuildToolVectorSkipDetail(embeddingConfigured, storePersistent);
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", "工具选择：向量索引未使用", skipDetail, ct).ConfigureAwait(false);
                    }
                    if (selectedPairs == null)
                    {
                        _agentDebugStats.RecordTwoStageUsed();
                        _logger.LogInformation("[{SessionId}] ToolSelection: using two-stage LLM path.", sessionId);
                        var twoStage = await _toolSelector.SelectFunctionsAsync(userMessage, recentHistory, kernel, ct).ConfigureAwait(false);
                        selectedPairs = twoStage.SelectedPairs;
                        _logger.LogInformation("[{SessionId}] ToolSelection: two-stage returned selectedPairsCount={Count}.",
                            sessionId, selectedPairs?.Count ?? -1);
                        var tsTrace = AgentTraceFormatter.BuildTwoStageToolTrace(twoStage);
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "toolSelection", tsTrace.Title, tsTrace.Detail, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Tool selection failed, using all tools.", sessionId);
                    _agentDebugStats.RecordToolSelectionException();
                    selectedPairs = null;
                    await NotifyAgentTraceAsync(
                        sessionManagerForStatus, sessionId, "toolSelection", "工具选择异常，已回退全量工具",
                        ErrorMessageHelper.GetFriendlyMessage(ex), ct).ConfigureAwait(false);
                }
            }
            IReadOnlyList<KernelFunction>? selectedFunctions;
            if (isPlanMode)
            {
                selectedFunctions = GetPlanOnlyFunctions(kernel);
                _logger.LogInformation("[{SessionId}] ToolSelection: plan mode, planOnlyFunctionCount={Count}.", sessionId, selectedFunctions?.Count ?? 0);
            }
            else
            {
                selectedFunctions = ResolveFunctionsByClientType(kernel, selectedPairs, clientType);
                if (planResult != null && selectedFunctions != null)
                    selectedFunctions = MergePlanFunctions(kernel, selectedFunctions);
                var pairsCount = selectedPairs?.Count ?? 0;
                var funcsCount = selectedFunctions?.Count ?? 0;
                var useAllTools = selectedFunctions == null || selectedFunctions.Count == 0;
                _logger.LogInformation("[{SessionId}] ToolSelection: ResolveFunctionsByClientType clientType={ClientType} selectedPairsCount={PairsCount} resolvedFunctionCount={FuncCount} useAllTools={UseAll}.",
                    sessionId, clientType ?? "(null)", pairsCount, funcsCount, useAllTools);
            }

            var maxOutputTokens = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
            OpenAIPromptExecutionSettings execSettings;
            if (selectedFunctions is { Count: > 0 })
            {
                execSettings = new OpenAIPromptExecutionSettings
                {
                    MaxTokens = maxOutputTokens,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(selectedFunctions)
                };
                _logger.LogInformation("[{SessionId}] ToolSelection: final restricted to {FunctionCount} functions clientType={ClientType}.",
                    sessionId, selectedFunctions.Count, clientType ?? "(null)");
                _logger.LogDebug("[{SessionId}] Tool selection: clientType={ClientType} {FunctionCount} functions",
                    sessionId, clientType ?? "(null)", selectedFunctions.Count);
            }
            else
            {
                execSettings = new OpenAIPromptExecutionSettings
                {
                    MaxTokens = maxOutputTokens,
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                };
                _logger.LogInformation("[{SessionId}] ToolSelection: final no restriction (all tools).", sessionId);
            }

            var fullResponse = new System.Text.StringBuilder();

            // 按 clientType 注入身份说明；非计划模式下追加「工具结果须在正文复述」约束
            var identitySuffix = GetClientTypeIdentitySuffix(clientType);
            var historyToUse = BuildHistoryForStreamingTurn(state.History, identitySuffix, isPlanMode);

            await NotifyAgentStatusAsync(
                sessionManagerForStatus,
                sessionId,
                isPlanMode ? "正在等待模型生成计划…" : "正在等待模型响应…",
                ct).ConfigureAwait(false);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                    fullResponse.Clear();

                await using var streamEnum = chat.GetStreamingChatMessageContentsAsync(
                    historyToUse, execSettings, kernel, ct).GetAsyncEnumerator(ct);

                var contextRetry = false;
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await streamEnum.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (attempt == 0 && !ctxConfig.PassThroughContext && ctxConfig.ContextLengthRetryEnabled && IsContextLengthError(ex))
                    {
                        _logger.LogWarning(ex, "[{SessionId}] Context length exceeded, retrying with halved budget.", sessionId);
                        TrimHistoryForRetry(state.History, ctxConfig.ContextLengthRetryMaxTurns, ctxConfig);
                        historyToUse = BuildHistoryForStreamingTurn(state.History, identitySuffix, isPlanMode);
                        contextRetry = true;
                        break;
                    }

                    if (!moved)
                        break;

                    var chunk = streamEnum.Current;
                    if (chunk.Content is { Length: > 0 } text)
                    {
                        fullResponse.Append(text);
                        yield return new StreamItem(IsWarning: false, Content: text);
                    }
                }

                if (contextRetry)
                    continue;

                break;
            }

            var assistantText = ReasoningTagStreamParser.StripReasoningTags(fullResponse.ToString());
            state.History.AddAssistantMessage(assistantText);
            var previewLen = Math.Min(200, assistantText.Length);
            var preview = previewLen > 0 ? assistantText.AsSpan(0, previewLen).ToString().Replace('\r', ' ').Replace('\n', ' ') : "";
            if (assistantText.Length > previewLen) preview += "…";
            _logger.LogInformation("[{SessionId}] Turn completed, turns={Turns}, assistantChars={AssistantChars}, assistantPreview={Preview}",
                sessionId, state.History.Count, assistantText.Length, preview);
        }
        finally { }
    }

    /// <summary>将最旧若干轮（最多 6 轮）压缩为一段摘要并替换为一条消息；若配置了落盘目录则先将被压缩的原文追加写入会话历史文件。</summary>
    private async Task<(bool DidCompact, int MessagesRemoved, int SummaryLength)> TrySummarizeOldTurnsAsync(ChatHistory history, Kernel kernel, IChatCompletionService chatService, ContextWindowConfig ctx, string sessionId, CancellationToken ct)
    {
        var dir = GetConversationHistoryDirectory(ctx);
        var r = await SummarizeOldTurnsCoreAsync(history, kernel, chatService, ctx, sessionId, dir, ct).ConfigureAwait(false);
        if (r.DidCompact)
            _logger.LogDebug("[{SessionId}] Summarized {MessagesRemoved} messages into one block.", sessionId, r.MessagesRemoved);
        return r;
    }

    /// <summary>执行摘要压缩核心逻辑：落盘、生成摘要、替换历史。返回是否执行、被移除的消息条数、摘要字符数。offloadDirectory 为空则不落盘。</summary>
    private static async Task<(bool DidCompact, int MessagesRemoved, int SummaryLength)> SummarizeOldTurnsCoreAsync(ChatHistory history, Kernel kernel, IChatCompletionService chatService, ContextWindowConfig ctx, string sessionId, string? offloadDirectory, CancellationToken ct)
    {
        const int maxTurnsToSummarize = 6;
        var toTake = Math.Min(maxTurnsToSummarize * 2, history.Count - 1);
        if (toTake < 4)
            return (false, 0, 0);
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
            return (false, 0, 0);

        var dir = offloadDirectory;
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
                var safeName = string.Join("_", (sessionId ?? "").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                if (string.IsNullOrEmpty(safeName)) safeName = "session";
                var path = Path.Combine(dir, safeName + ".md");
                var section = $"\n\n## Summarized at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n\n{input}\n\n";
                await File.AppendAllTextAsync(path, section, ct).ConfigureAwait(false);
            }
            catch { /* offload best-effort */ }
        }

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
            return (false, 0, 0);
        if (summary.Length > ctx.SummarizationMaxSummaryChars)
            summary = summary.AsSpan(0, ctx.SummarizationMaxSummaryChars).ToString() + "…";
        for (var i = 0; i < toTake; i++)
            history.RemoveAt(1);
        history.Insert(1, new ChatMessageContent(AuthorRole.User, "[此前对话摘要]\n" + summary));
        return (true, toTake, summary.Length);
    }

    /// <summary>供 run_subtask 工具调用：在隔离的上下文中执行子任务，仅将最终自然语言结果返回给主 Agent，不把子任务内的多轮 tool 调用塞入主会话历史。</summary>
    public async Task<string> RunSubtaskAsync(string sessionId, string taskDescription, string? constraints, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "[错误] 无当前会话，无法执行子任务。";
        if (string.IsNullOrWhiteSpace(taskDescription))
            return "[错误] 子任务描述不能为空。";
        var taskDescTrimmed = taskDescription.Trim();
        var kernel = _kernelAccessor.Kernel;
        if (kernel == null)
            return "[错误] 内核未就绪。";
        var chat = kernel.Services.GetKeyedService<IChatCompletionService>(_kernelAccessor.ActiveModelId)
            ?? kernel.Services.GetService<IChatCompletionService>();
        if (chat == null)
            return "[错误] 未找到对话服务。";
        var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
        var clientType = sessionManager.GetClientType(sessionId);
        var allFunctions = ClientTypeToolFilter.GetAllowedFunctions(kernel, clientType);
        // 排除 run_subtask（防递归）和 compact_conversation（防子代理操作主会话历史）
        var allowedFunctions = allFunctions
            .Where(f => !string.Equals(f.Name, "run_subtask", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(f.Name, "compact_conversation", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (allowedFunctions.Count == 0)
            return "[错误] 当前端无可用的工具集，无法执行子任务。";

        var systemPrompt = "你是一个子代理。请完成用户给出的子任务，可使用现有工具。完成后仅用一段自然语言总结最终结果，不要逐步解释过程。";
        var userContent = taskDescTrimmed;
        if (!string.IsNullOrWhiteSpace(constraints))
            userContent += "\n\n约束：" + constraints.Trim();
        var subHistory = new ChatHistory(systemPrompt);
        subHistory.AddUserMessage(userContent);

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(allowedFunctions),
            MaxTokens = 4096
        };
        var fullResponse = new System.Text.StringBuilder();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        SubtaskContext.SetActive(true);
        try
        {
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage
            {
                Type = "subtask_start",
                TaskDescription = taskDescTrimmed,
                Constraints = string.IsNullOrWhiteSpace(constraints) ? null : constraints.Trim()
            }).ConfigureAwait(false);
            await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(subHistory, settings, kernel, timeoutCts.Token).ConfigureAwait(false))
            {
                if (chunk.Content is { Length: > 0 } text)
                {
                    fullResponse.Append(text);
                    await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_chunk", Content = text }).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("[RunSubtask] SessionId={SessionId} timed out after 120s", sessionId);
            var partial = fullResponse.ToString().Trim();
            var timeoutMsg = string.IsNullOrEmpty(partial) ? "[子任务超时] 子代理执行超过 120 秒，已中止。" : $"[子任务超时] 部分结果：{partial}";
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_end", Content = timeoutMsg }).ConfigureAwait(false);
            return timeoutMsg;
        }
        catch (OperationCanceledException)
        {
            const string userStopMsg = "[子任务已由用户停止]";
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_end", Content = userStopMsg }).ConfigureAwait(false);
            return userStopMsg;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RunSubtask] SessionId={SessionId} task failed", sessionId);
            var failMsg = $"[子任务执行失败] {ex.Message}";
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_end", Content = failMsg }).ConfigureAwait(false);
            return failMsg;
        }
        finally
        {
            SubtaskContext.SetActive(false);
        }
        var result = fullResponse.ToString().Trim();
        var endContent = string.IsNullOrEmpty(result) ? null : result;
        await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_end", Content = endContent ?? "" }).ConfigureAwait(false);
        return string.IsNullOrEmpty(result) ? "[子任务未返回文本结果]" : result;
    }

    private static async Task SendSubtaskMessageAsync(SessionManager sessionManager, string sessionId, WsMessage msg)
    {
        try
        {
            var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
            await sessionManager.SendToAsync(sessionId, json).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 推送失败不阻塞子任务执行，仅前端收不到流式展示
        }
    }

    /// <summary>供 compact_conversation 工具调用：主动压缩当前会话的最旧若干轮为摘要。返回可展示给模型的结果文案。</summary>
    public async Task<string> CompactConversationAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "[错误] 无当前会话，无法压缩。";
        if (!_sessions.TryGetValue(sessionId, out var state))
            return "[错误] 会话不存在或已过期。";
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        var kernel = _kernelAccessor.Kernel;
        if (kernel == null)
            return "[错误] 内核未就绪。";
        var chat = kernel.Services.GetKeyedService<IChatCompletionService>(_kernelAccessor.ActiveModelId)
            ?? kernel.Services.GetService<IChatCompletionService>();
        if (chat == null)
            return "[错误] 未找到对话服务。";
        var dir = GetConversationHistoryDirectory(ctx);
        var (didCompact, messagesRemoved, _) = await SummarizeOldTurnsCoreAsync(state.History, kernel, chat, ctx, sessionId, dir, ct).ConfigureAwait(false);
        if (didCompact)
        {
            var sessionManager = _serviceProvider.GetService<SessionManager>();
            if (sessionManager != null && messagesRemoved > 0)
            {
                var summaryLen = state.History.Count > 1 ? (state.History[1].Content?.Length ?? 0) : 0;
                var offloadConfigured = !string.IsNullOrWhiteSpace(dir);
                var ctxTrace = AgentTraceFormatter.BuildContextSummarizationSuccessTrace(messagesRemoved, summaryLen, ctx, offloadConfigured);
                await NotifyAgentTraceAsync(sessionManager, sessionId, "context", ctxTrace.Title, ctxTrace.Detail, ct).ConfigureAwait(false);
            }
            var turns = Math.Max(1, messagesRemoved / 2);
            return $"[已压缩] 已将最近约 {turns} 轮对话合并为一段摘要，上下文已释放。";
        }
        if (state.History.Count <= 3)
            return "[无需压缩] 当前对话轮次较少，无需压缩。";
        return "[未压缩] 当前对话轮次或内容不足，未执行压缩。";
    }

    /// <summary>解析摘要落盘目录：优先 ContextWindow.ConversationHistoryDirectory，否则与 PlansDirectory 同级的 ConversationHistory，再否则 %LocalAppData%/OfficeCopilot/ConversationHistory。</summary>
    private string? GetConversationHistoryDirectory(ContextWindowConfig ctx)
    {
        var dir = (ctx.ConversationHistoryDirectory ?? "").Trim();
        if (dir.Length > 0)
        {
            dir = Environment.ExpandEnvironmentVariables(dir);
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(AppContext.BaseDirectory, dir);
            return dir;
        }
        var plansDir = (_configService.Current.PlansDirectory ?? "").Trim();
        if (plansDir.Length > 0)
        {
            plansDir = Environment.ExpandEnvironmentVariables(plansDir);
            if (!Path.IsPathRooted(plansDir))
                plansDir = Path.Combine(AppContext.BaseDirectory, plansDir);
            var parent = Path.GetDirectoryName(plansDir);
            if (!string.IsNullOrEmpty(parent))
                return Path.Combine(parent, "ConversationHistory");
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "OfficeCopilot", "ConversationHistory");
    }

    private static bool IsContextLengthError(Exception ex)
    {
        var msg = (ex.Message ?? "").ToLowerInvariant();
        return msg.Contains("context_length") || msg.Contains("maximum context") || msg.Contains("token limit")
            || msg.Contains("too many tokens");
    }

    /// <summary>为 context_length 重试裁剪历史：先按轮数限制，再按预算减半裁剪。</summary>
    private static void TrimHistoryForRetry(ChatHistory history, int maxTurns, ContextWindowConfig ctx)
    {
        var keepMessages = 1 + Math.Max(0, maxTurns) * 2;
        while (history.Count > keepMessages)
            history.RemoveAt(1);
        var halfBudget = (ctx.MaxContextTokens - ctx.ReservedOutputTokens) / 2;
        if (halfBudget <= 0) return;
        var total = 0;
        for (var i = 0; i < history.Count; i++)
            total += TokenEstimator.EstimateTokens(history[i].Content ?? "", ctx);
        while (total > halfBudget && history.Count > 3)
        {
            var removed = TokenEstimator.EstimateTokens(history[1].Content ?? "", ctx);
            history.RemoveAt(1);
            total -= removed;
        }
    }

    /// <summary>估算整个历史的 token 总数，含 ImageContent 的视觉 token 估算。</summary>
    private static int EstimateHistoryTokens(ChatHistory history, ContextWindowConfig ctx)
    {
        var total = 0;
        for (var i = 0; i < history.Count; i++)
            total += EstimateMessageTokens(history[i], ctx);
        return total;
    }

    /// <summary>估算单条消息的 token 数，含 ImageContent 的视觉 token 估算。</summary>
    private static int EstimateMessageTokens(ChatMessageContent msg, ContextWindowConfig ctx)
    {
        var tokens = TokenEstimator.EstimateTokens(msg.Content ?? "", ctx);
        if (msg.Items is { Count: > 0 })
        {
            foreach (var item in msg.Items)
            {
                if (item is Microsoft.SemanticKernel.TextContent text)
                    tokens += TokenEstimator.EstimateTokens(text.Text ?? "", ctx);
                else if (item is Microsoft.SemanticKernel.ImageContent)
                    tokens += TokenEstimator.EstimateImageTokens(1024, 1024);
            }
        }
        return tokens;
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
        var maxMessagesByTurns = 1 + session.MaxHistoryTurns * 2;

        // 先按轮数上限裁（未配置 token 或未超 token 时也遵守轮数上限）
        while (history.Count > maxMessagesByTurns)
        {
            history.RemoveAt(1);
        }

        if (ctx.PassThroughContext)
            return;

        var maxContextTokens = GetEffectiveMaxContextTokens();
        if (maxContextTokens <= 0)
            return;

        var budget = maxContextTokens - ctx.ReservedOutputTokens;
        if (budget <= 0)
            return;

        var totalTokens = EstimateHistoryTokens(history, ctx);

        var minMessagesToKeep = 1 + Math.Max(0, session.MinTurnsToKeep) * 2;
        while (totalTokens > budget && history.Count > minMessagesToKeep)
        {
            if (history.Count <= 2)
                break;
            var removed = EstimateMessageTokens(history[1], ctx) + (history.Count > 2 ? EstimateMessageTokens(history[2], ctx) : 0);
            history.RemoveAt(1);
            if (history.Count > 1)
                history.RemoveAt(1);
            totalTokens -= removed;
        }
    }

    /// <summary>按 clientType 解析本轮暴露给模型的工具：使用 selectedPairs，或该端全量允许的函数。保底追加：Office/WPS 追加 current_run_document_script；Chrome 追加 run_page_script；所有端追加 run_command。</summary>
    private static IReadOnlyList<KernelFunction>? ResolveFunctionsByClientType(
        Kernel kernel,
        IReadOnlyList<(string PluginName, string FunctionName)>? selectedPairs,
        string? clientType)
    {
        IReadOnlyList<KernelFunction>? result = null;
        if (selectedPairs is { Count: > 0 })
        {
            var filtered = ClientTypeToolFilter.Filter(selectedPairs, clientType);
            if (filtered.Count > 0)
                result = GetFunctionsByPluginAndFunctionNames(kernel, filtered);
        }
        if (result == null)
            result = ClientTypeToolFilter.GetAllowedFunctions(kernel, clientType);

        // 保底工具追加（顺序：Office/WPS 文档脚本 → Chrome 页面脚本 → 所有端 CLI）
        if (result != null)
        {
            // 1. Office/WPS 端：始终追加 current_run_document_script 与 current_run_custom_document_script，不参与阶段一子类选择
            if (IsOfficeOrWpsClient(clientType))
            {
                if (kernel.Plugins.TryGetFunction("CurrentDocument", "current_run_document_script", out var docScript) && docScript != null &&
                    !result.Any(f => string.Equals(f.Name, "current_run_document_script", StringComparison.OrdinalIgnoreCase)))
                    result = new List<KernelFunction>(result) { docScript };
                if (kernel.Plugins.TryGetFunction("CurrentDocument", "current_run_custom_document_script", out var customDocScript) && customDocScript != null &&
                    !result.Any(f => string.Equals(f.Name, "current_run_custom_document_script", StringComparison.OrdinalIgnoreCase)))
                    result = new List<KernelFunction>(result) { customDocScript };
            }

            // 2. Chrome 端：兜底仅追加 run_custom_page_script（AI 生成脚本并执行）；run_page_script 为预定义脚本工具，不参与兜底
            if (IsChromeClient(clientType) &&
                kernel.Plugins.TryGetFunction("Browser", "run_custom_page_script", out var customPageScript) && customPageScript != null &&
                !result.Any(f => string.Equals(f.Name, "run_custom_page_script", StringComparison.OrdinalIgnoreCase)))
            {
                result = new List<KernelFunction>(result) { customPageScript };
            }

            // 3. 所有端：始终追加 run_command 作为本机命令兜底
            if (kernel.Plugins.TryGetFunction("CLI", "run_command", out var cliRun) && cliRun != null &&
                !result.Any(f => string.Equals(f.Name, "run_command", StringComparison.OrdinalIgnoreCase)))
            {
                result = new List<KernelFunction>(result) { cliRun };
            }
        }
        return result;
    }

    private static bool IsChromeClient(string? clientType)
    {
        var ct = (clientType ?? "").Trim();
        return string.IsNullOrEmpty(ct) || string.Equals(ct, "chrome", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 非计划模式下注入：用户界面看不到工具原始返回全文，模型必须在最终回复中整理复述。
    /// </summary>
    private const string ToolResultEchoSystemInstruction =
        "[工具与回答方式] 用户对话界面中看不到工具的原始返回全文（执行过程里可能仅有简短摘要）。"
        + "凡你调用了工具并从工具结果中获得了对用户有用的文字或数据，在本轮最终回复里必须用自然语言完整整理并复述给用户；"
        + "禁止仅用「已读取」「已完成」等占位描述而不给出实质内容。";

    /// <summary>
    /// 构建本轮流式请求用的 ChatHistory：可选追加 client 身份后缀；非计划模式再追加工具结果复述约束。
    /// </summary>
    private static ChatHistory BuildHistoryForStreamingTurn(ChatHistory stateHistory, string? identitySuffix, bool isPlanMode)
    {
        var historyToUse = stateHistory;
        if (!string.IsNullOrEmpty(identitySuffix) && stateHistory.Count > 0 && stateHistory[0].Role == AuthorRole.System)
        {
            var sysMsg = stateHistory[0];
            var newSystemText = (sysMsg.Content ?? "") + "\n\n" + identitySuffix;
            var newHistory = new ChatHistory(newSystemText);
            for (var i = 1; i < stateHistory.Count; i++)
                newHistory.Add(stateHistory[i]);
            historyToUse = newHistory;
        }

        if (!isPlanMode && historyToUse.Count > 0 && historyToUse[0].Role == AuthorRole.System)
        {
            var sys = historyToUse[0].Content ?? "";
            var augmented = sys + "\n\n" + ToolResultEchoSystemInstruction;
            var withEcho = new ChatHistory(augmented);
            for (var i = 1; i < historyToUse.Count; i++)
                withEcho.Add(historyToUse[i]);
            historyToUse = withEcho;
        }

        return historyToUse;
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
        if (string.Equals(ct, "office-powerpoint", StringComparison.OrdinalIgnoreCase))
            return "你是 PowerPoint 侧助手，负责当前打开的 PowerPoint 演示文稿；网页相关操作请由用户在浏览器侧边栏端完成。";
        return "";
    }

    private static bool IsOfficeOrWpsClient(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)) return false;
        var ct = clientType.Trim();
        return string.Equals(ct, "office-word", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "office-excel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "office-powerpoint", StringComparison.OrdinalIgnoreCase)
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

    /// <summary>将 Plan 插件的 get_plan、update_plan、execute_plan_step、complete_plan 并入已选函数列表（供按计划执行时使用）。</summary>
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
                    string.Equals(func.Name, "execute_plan_step", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(func.Name, "complete_plan", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>向量检索用：截断并拼接最近一条历史，与两轮选择一致。</summary>
    private static string BuildToolSelectionUserPrompt(string userMessage, ChatHistory? recentHistory)
    {
        var userPrompt = (userMessage ?? "").Trim();
        if (userPrompt.Length > 1000)
            userPrompt = userPrompt[..1000] + "...";
        if (recentHistory != null && recentHistory.Count > 0)
        {
            var lastContent = recentHistory[^1].Content ?? "";
            if (lastContent.Length > 0 && lastContent.Length < 500)
                userPrompt = userPrompt + "\n[上一条] " + lastContent;
        }
        return userPrompt;
    }

    /// <summary>将向量检索结果与 AlwaysIncludePlugins 合并，与 ToolSelectionService 两轮路径语义一致。</summary>
    private static IReadOnlyList<(string PluginName, string FunctionName)> MergeVectorResultsWithAlwaysInclude(
        IReadOnlyList<(string PluginName, string FunctionName)> fromVector,
        AiConfig ai,
        Kernel kernel)
    {
        var result = new HashSet<(string, string)>(fromVector, new PluginFunctionComparer());
        var alwaysPlugins = ai.AlwaysIncludePlugins ?? new List<string>();
        var alwaysSet = new HashSet<string>(alwaysPlugins.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in kernel.Plugins)
        {
            if (!alwaysSet.Contains(plugin.Name)) continue;
            foreach (KernelFunction func in plugin)
                result.Add((plugin.Name, func.Name));
        }
        return result.ToList();
    }

    private sealed class PluginFunctionComparer : IEqualityComparer<(string Plugin, string Function)>
    {
        public bool Equals((string Plugin, string Function) x, (string Plugin, string Function) y) =>
            string.Equals(x.Plugin, y.Plugin, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Function, y.Function, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Plugin, string Function) obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Plugin) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Function);
    }

    /// <summary>向当前会话 WebSocket 推送一行「正在干什么」，供前端活动条展示。</summary>
    private static async Task NotifyAgentStatusAsync(SessionManager sessionManager, string sessionId, string text, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(sessionId)) return;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return;
        if (t.Length > 200)
            t = t.Substring(0, 200);
        var msg = new WsMessage { Type = "agent_status", Content = t };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await sessionManager.SendToAsync(sessionId, json).ConfigureAwait(false);
    }

    /// <summary>向当前会话推送结构化内部过程，供时间线展示与联调。</summary>
    private static async Task NotifyAgentTraceAsync(
        SessionManager sessionManager,
        string sessionId,
        string traceCategory,
        string traceTitle,
        string? traceDetail,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(sessionId)) return;
        var cat = (traceCategory ?? "").Trim();
        if (cat.Length == 0) return;
        var title = AgentTraceFormatter.TruncateTitle(traceTitle);
        if (title.Length == 0) return;
        var detail = AgentTraceFormatter.TruncateDetail(traceDetail);
        var msg = new WsMessage
        {
            Type = "agent_trace",
            Content = title,
            TraceCategory = cat,
            TraceTitle = title,
            TraceDetail = string.IsNullOrEmpty(detail) ? null : detail
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await sessionManager.SendToAsync(sessionId, json).ConfigureAwait(false);
    }

    private void CleanupExpiredSessions(object? _)
    {
        var session = _configService.Current.Session ?? new SessionConfig();
        var cutoff = DateTime.UtcNow.AddMinutes(-session.TimeoutMinutes);
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
