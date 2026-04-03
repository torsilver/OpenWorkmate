using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
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
using OfficeCopilot.Server.Services.DashScope;
using OfficeCopilot.Server.Mcp;
using OfficeCopilot.Server.Services.SemanticKernel;

namespace OfficeCopilot.Server;

public sealed partial class ChatService : IDisposable
{
    private Kernel _kernel = null!;
    /// <summary>当前选中的模型 Id，用于按 key 解析 IChatCompletionService。</summary>
    private string _activeModelId = "";
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly System.Threading.Timer _cleanupTimer;
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
    private readonly SkStreamChatToolingProcessRegistry _skToolingRegistry;
    private readonly SkSubtaskChatCompletionAgentRunner _skSubtaskAgentRunner;
    private readonly IChatTurnProcessCoordinator _turnCoordinator;
    private readonly object _kernelLock = new();

    public ChatService(IConfiguration config, ILogger<ChatService> logger, ILoggerFactory loggerFactory, ConfigService configService, SkillService skillService, McpClientManager mcpManager, IToolSelector toolSelector, IToolIndexService toolIndex, IVectorStore vectorStore, IServiceProvider serviceProvider, IKernelAccessor kernelAccessor, EmbeddingProvider embeddingProvider, IPlanStore planStore, AgentDebugStatsService agentDebugStats, SkStreamChatToolingProcessRegistry skToolingRegistry, SkSubtaskChatCompletionAgentRunner skSubtaskAgentRunner, IChatTurnProcessCoordinator turnCoordinator)
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
        _skToolingRegistry = skToolingRegistry;
        _skSubtaskAgentRunner = skSubtaskAgentRunner;
        _turnCoordinator = turnCoordinator;

        var session = configService.Current.Session ?? new SessionConfig();
        var cleanupInterval = session.CleanupIntervalMinutes;

        _configService.OnConfigChanged += () => _ = RebuildKernelAsync(skipUserToolIndexSync: false);
        _skillService.OnSkillsChanged += () => _ = RebuildKernelAsync(skipUserToolIndexSync: false);

        _cleanupTimer = new System.Threading.Timer(CleanupExpiredSessions, null,
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

                var logHandler = new OpenAiLoggingHandler(_loggerFactory.CreateLogger<OpenAiLoggingHandler>());
                var dashHandler = new DashScopeOpenAiCompatHandler(
                    _configService,
                    entry.Id,
                    logHandler,
                    _loggerFactory.CreateLogger<DashScopeOpenAiCompatHandler>());
                var httpClient = new HttpClient(dashHandler);

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

        builder.Services.AddSingleton(_skToolingRegistry);
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
        newKernel.FunctionInvocationFilters.Add(new ToolStatusFilter(sessionManager, _configService, _loggerFactory.CreateLogger<ToolStatusFilter>(), _agentDebugStats));

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

        // Tavily 原生插件：未停用时始终注册；Key 来自 user-config 的 tavilyApiKey 或 skillEnv.TAVILY_API_KEY
        var tavilyApiKey = (_configService.Current.TavilyApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(tavilyApiKey) && _configService.Current.SkillEnv != null && _configService.Current.SkillEnv.TryGetValue("TAVILY_API_KEY", out var fromSkillEnv) && !string.IsNullOrEmpty(fromSkillEnv))
            tavilyApiKey = fromSkillEnv.Trim();
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

        if (!disabledBuiltIn.Contains("meetingtranscript"))
        {
            var meetingStore = _serviceProvider.GetRequiredService<IMeetingTranscriptStore>();
            newKernel.Plugins.AddFromObject(new MeetingTranscriptPlugin(meetingStore), "MeetingTranscript");
        }

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
                var wrapper = new McpKernelPlugin(client, $"MCP_{mcpConfig.Name}", _loggerFactory.CreateLogger<McpKernelPlugin>());
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

    /// <summary>追加到主对话 system：Memory / AccurateData / Plan / UserOptions 分工与双触发（用户可点名 + 模型可按需启用）。宜保持精炼；完整默认 system 见 ConfigService 与 docs/提示词清单.md（Harness：避免与 AiConfig 长文重复堆叠）。</summary>
    private const string BuiltinTaskPluginSystemGuidance = """
[内置插件：记忆 / 准确数据 / 计划 / 候选项确认]
以下均为内置能力（非外接 MCP）。用户可在对话中明确要求；你也应在符合条件时主动选用对应工具。

- Memory：记录与检索用户的习惯、取向、偏好与长期关键事实；不用于存大块中间数据或任务步骤正文。
- AccurateData：多步复杂任务中按固定 id 精确读写大块结构化中间结果以减轻上下文；不替代语义记忆或计划步骤流。
- Plan：将复杂任务拆解为可保存、可按步执行的计划（Markdown 步骤）；不替代 AccurateData 存原始数据块，不替代 Memory 记偏好。
- UserOptions（工具 ask_options）：当存在多种合规路径、输出格式或分步选择，或缺少关键参数不宜替用户拍板时，优先用侧栏分步单选（stepsJson）让用户确认；勿在信息不足时猜测默认。勿要求用户在聊天里回复「选 A/B」。

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
                var activeEntry = GetActiveModelEntry();
                var supportsVision = activeEntry?.SupportsVision == true;
                var attachmentCache = _serviceProvider.GetRequiredService<AttachmentCacheService>();
                var userMsg = AttachmentRefChatMessageFactory.Build(
                    userMessage,
                    attachmentRefs,
                    supportsVision,
                    attachmentCache,
                    _logger);
                state.History.Add(userMsg);
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

            var ctxForAudit = _configService.Current.ContextWindow ?? new ContextWindowConfig();
            SessionAuditLog.TryAppend(ctxForAudit, sessionId, "user_message", new
            {
                contentPreview = SessionAuditLog.SanitizeForAudit(userMessage),
                mode,
                planId,
                attachmentCount = attachmentRefs?.Count ?? attachments?.Count ?? 0
            });

            TrimHistory(state.History);

            var sessionManagerForStatus = _serviceProvider.GetRequiredService<SessionManager>();
            await NotifyAgentStatusAsync(sessionManagerForStatus, sessionId, "正在准备上下文…", ct).ConfigureAwait(false);

            var ctxConfig = _configService.Current.ContextWindow ?? new ContextWindowConfig();
            var turn = new StreamChatTurnContext
            {
                SessionId = sessionId,
                UserMessage = userMessage,
                KnowledgeBaseId = knowledgeBaseId,
                Mode = mode,
                PlanId = planId,
                PlanCurrentStepIndex = planCurrentStepIndex,
                State = state,
                Kernel = kernel,
                Chat = chat,
                SessionManager = sessionManagerForStatus,
                CtxConfig = ctxConfig
            };

            var skFeat = _configService.Current.SemanticKernel;
            var ctxProc = skFeat?.UseLocalProcessForStreamChatContext == true;
            var toolProc = skFeat?.UseLocalProcessForStreamChatTooling == true;

            async Task RunContextBothPartsAsync()
            {
                await _turnCoordinator.RunContextPreparationPart1Async(turn, ct).ConfigureAwait(false);
                await _turnCoordinator.RunContextPreparationPart2Async(turn, ct).ConfigureAwait(false);
            }

            async Task RunToolingAsync() =>
                await _turnCoordinator.RunToolingPhaseAsync(turn, ct).ConfigureAwait(false);

            if (ctxProc && toolProc)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                _skToolingRegistry.Register(correlationId, RunContextBothPartsAsync, RunToolingAsync);
                var fullProcessFailed = false;
                try
                {
                    await _skToolingRegistry.RunFullStreamChatProcessAsync(kernel, correlationId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] SK Process 全阶段失败，回退内联。", sessionId);
                    fullProcessFailed = true;
                }
                finally
                {
                    _skToolingRegistry.Unregister(correlationId);
                }
                if (fullProcessFailed)
                {
                    await _turnCoordinator.RunContextPreparationPart1Async(turn, ct).ConfigureAwait(false);
                    foreach (var w in turn.ContextWarnings)
                        yield return new StreamItem(IsWarning: true, Content: w);
                    turn.ContextWarnings.Clear();
                    await _turnCoordinator.RunContextPreparationPart2Async(turn, ct).ConfigureAwait(false);
                    await RunToolingAsync().ConfigureAwait(false);
                }
                else
                {
                    foreach (var w in turn.ContextWarnings)
                        yield return new StreamItem(IsWarning: true, Content: w);
                    turn.ContextWarnings.Clear();
                }
            }
            else if (ctxProc && !toolProc)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                _skToolingRegistry.Register(correlationId, RunContextBothPartsAsync, null);
                var contextProcessFailed = false;
                try
                {
                    await _skToolingRegistry.RunContextOnlyProcessAsync(kernel, correlationId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] SK Process 上下文阶段失败，回退内联。", sessionId);
                    contextProcessFailed = true;
                }
                finally
                {
                    _skToolingRegistry.Unregister(correlationId);
                }
                if (contextProcessFailed)
                {
                    await _turnCoordinator.RunContextPreparationPart1Async(turn, ct).ConfigureAwait(false);
                    foreach (var w in turn.ContextWarnings)
                        yield return new StreamItem(IsWarning: true, Content: w);
                    turn.ContextWarnings.Clear();
                    await _turnCoordinator.RunContextPreparationPart2Async(turn, ct).ConfigureAwait(false);
                }
                foreach (var w in turn.ContextWarnings)
                    yield return new StreamItem(IsWarning: true, Content: w);
                turn.ContextWarnings.Clear();
                await RunToolingAsync().ConfigureAwait(false);
            }
            else if (!ctxProc && toolProc)
            {
                await _turnCoordinator.RunContextPreparationPart1Async(turn, ct).ConfigureAwait(false);
                foreach (var w in turn.ContextWarnings)
                    yield return new StreamItem(IsWarning: true, Content: w);
                turn.ContextWarnings.Clear();
                await _turnCoordinator.RunContextPreparationPart2Async(turn, ct).ConfigureAwait(false);
                var correlationId = Guid.NewGuid().ToString("N");
                _skToolingRegistry.Register(correlationId, RunToolingAsync);
                try
                {
                    await _skToolingRegistry.RunToolingProcessAsync(kernel, correlationId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] SK Process 工具阶段失败，回退内联执行。", sessionId);
                    await RunToolingAsync().ConfigureAwait(false);
                }
                finally
                {
                    _skToolingRegistry.Unregister(correlationId);
                }
            }
            else
            {
                await _turnCoordinator.RunContextPreparationPart1Async(turn, ct).ConfigureAwait(false);
                foreach (var w in turn.ContextWarnings)
                    yield return new StreamItem(IsWarning: true, Content: w);
                turn.ContextWarnings.Clear();
                await _turnCoordinator.RunContextPreparationPart2Async(turn, ct).ConfigureAwait(false);
                await RunToolingAsync().ConfigureAwait(false);
            }

            var fullResponse = new System.Text.StringBuilder();

            await NotifyAgentStatusAsync(
                sessionManagerForStatus,
                sessionId,
                "正在等待模型响应…",
                ct).ConfigureAwait(false);

            if (skFeat?.UseAgentGroupChatMainSession == true)
            {
                ToolInvocationTurnMeter.BeginTurn(sessionId);
                try
                {
                    await foreach (var streamItem in SkMainSessionAgentGroupChatRunner.InvokeStreamingAsync(
                                       kernel, _loggerFactory, turn.HistoryToUse, turn.ExecSettings, turn.SessionManager, sessionId, ct)
                                   .ConfigureAwait(false))
                    {
                        if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                            fullResponse.Append(streamItem.Content);
                        yield return streamItem;
                    }
                }
                finally
                {
                    ToolInvocationTurnMeter.EndTurn(sessionId);
                }
            }
            else
            {
                if (skFeat?.UseHostPreambleAgent == true)
                {
                    var preambleSb = new System.Text.StringBuilder();
                    var hostPreambleSettings = new OpenAIPromptExecutionSettings { MaxTokens = 256, Temperature = 0.3f };
                    var hostPreambleAgent = new ChatCompletionAgent
                    {
                        Name = "HostBrief",
                        Instructions = "用一两句话说明你将如何组织回答（不写正文解答）。",
                        Kernel = kernel,
                        LoggerFactory = _loggerFactory,
                        Arguments = new KernelArguments(hostPreambleSettings)
                    };
                    var preambleHistory = new ChatHistory();
                    preambleHistory.AddUserMessage(userMessage);
                    var preambleOpts = new AgentInvokeOptions { Kernel = kernel };
                    try
                    {
                        await foreach (var item in hostPreambleAgent.InvokeStreamingAsync(preambleHistory, thread: null, preambleOpts, ct).ConfigureAwait(false))
                        {
                            if (item.Message?.Content is { Length: > 0 } t)
                                preambleSb.Append(t);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{SessionId}] Host 前言 Agent 失败，跳过注入。", sessionId);
                    }
                    var preambleText = preambleSb.ToString().Trim();
                    if (preambleText.Length > 0)
                    {
                        await NotifyAgentTraceAsync(sessionManagerForStatus, sessionId, "agent_phase", "Host 前言", preambleText, ct).ConfigureAwait(false);
                        turn.HistoryToUse = InjectHostPreambleIntoStreamingHistory(turn.HistoryToUse, preambleText);
                    }
                }

                var dashScopeReasoningYieldedFromContext = new DashScopeReasoningCounter();
                ToolInvocationTurnMeter.BeginTurn(sessionId);
                try
                {
                    var streamOutcome = new StreamingPassOutcome();
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        if (attempt > 0)
                            fullResponse.Clear();

                        streamOutcome.ContextLengthRetryRequested = false;
                        await foreach (var streamItem in EnumerateMainModelStreamAsync(
                                           chat,
                                           turn.HistoryToUse,
                                           turn.ExecSettings,
                                           kernel,
                                           sessionId,
                                           ctxConfig,
                                           state,
                                           fullResponse,
                                           streamOutcome,
                                           attempt,
                                           dashScopeReasoningYieldedFromContext,
                                           ct).ConfigureAwait(false))
                        {
                            yield return streamItem;
                        }

                        if (streamOutcome.ContextLengthRetryRequested)
                        {
                            turn.HistoryToUse = BuildHistoryForStreamingTurn(state.History, turn.IdentitySuffix);
                            continue;
                        }

                        break;
                    }

                    // 工具接地重试：仅依据「本轮是否真实执行过 Kernel 工具」+ 用户原文启发式 + 可用函数列表。
                    // 不得使用推理流（reasoning_content / StreamSegmentKind.Reasoning）条数或文本参与此判断。
                    var firstPassTools = ToolInvocationTurnMeter.GetCount(sessionId);
                    var clientTypeForTools = sessionManagerForStatus.GetClientType(sessionId);
                    IReadOnlyList<KernelFunction> funcsForRequired = turn.SelectedKernelFunctions is { Count: > 0 }
                        ? turn.SelectedKernelFunctions
                        : ClientTypeToolFilter.GetAllowedFunctions(kernel, clientTypeForTools, sessionId);
                    var needToolGroundingRetry = firstPassTools == 0
                        && MutationIntentHeuristic.LikelyRequiresLocalMutationTool(userMessage)
                        && funcsForRequired is { Count: > 0 };

                    if (needToolGroundingRetry)
                    {
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry: firstPassTools=0 mutationIntent=true functionsForRequired={FnCount}",
                            sessionId, funcsForRequired.Count);
                        yield return new StreamItem(
                            IsWarning: true,
                            Content: "首轮响应未触发本机文件类工具调用；正在自动续跑一轮以完成操作…");

                        var retryHistory = CloneChatHistory(turn.HistoryToUse);
                        retryHistory.AddAssistantMessage(ReasoningTagStreamParser.StripReasoningTags(fullResponse.ToString()));
                        retryHistory.AddUserMessage(ToolGroundingRetryMessages.NudgeUserMessage);
                        fullResponse.Clear();
                        ToolInvocationTurnMeter.ResetCount(sessionId);

                        var maxTok = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
                        OpenAIPromptExecutionSettings retrySettings = new()
                        {
                            MaxTokens = maxTok,
                            FunctionChoiceBehavior = FunctionChoiceBehavior.Required(funcsForRequired, autoInvoke: true)
                        };
                        CopyOptionalPromptSettings(turn.ExecSettings, retrySettings);

                        var requiredApiFallback = false;
                        var requiredPassBuffer = new List<StreamItem>();
                        streamOutcome.ContextLengthRetryRequested = false;
                        try
                        {
                            await foreach (var streamItem in EnumerateMainModelStreamAsync(
                                               chat,
                                               retryHistory,
                                               retrySettings,
                                               kernel,
                                               sessionId,
                                               ctxConfig,
                                               state,
                                               fullResponse,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               dashScopeReasoningYieldedFromContext,
                                               ct).ConfigureAwait(false))
                            {
                                requiredPassBuffer.Add(streamItem);
                            }
                        }
                        catch (Exception ex) when (IsLikelyToolChoiceOrFunctionCallApiError(ex))
                        {
                            _logger.LogWarning(ex, "[{SessionId}] tool_choice Required 重试失败，回退为 Auto + 强提示。", sessionId);
                            requiredApiFallback = true;
                            requiredPassBuffer.Clear();
                            fullResponse.Clear();
                        }

                        foreach (var streamItem in requiredPassBuffer)
                            yield return streamItem;

                        if (requiredApiFallback)
                        {
                            ToolInvocationTurnMeter.ResetCount(sessionId);
                            fullResponse.Clear();
                            var fallbackSettings = new OpenAIPromptExecutionSettings
                            {
                                MaxTokens = maxTok,
                                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(funcsForRequired)
                            };
                            CopyOptionalPromptSettings(turn.ExecSettings, fallbackSettings);
                            streamOutcome.ContextLengthRetryRequested = false;
                            await foreach (var streamItem in EnumerateMainModelStreamAsync(
                                               chat,
                                               retryHistory,
                                               fallbackSettings,
                                               kernel,
                                               sessionId,
                                               ctxConfig,
                                               state,
                                               fullResponse,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               dashScopeReasoningYieldedFromContext,
                                               ct).ConfigureAwait(false))
                            {
                                yield return streamItem;
                            }
                        }

                        var retryPassTools = ToolInvocationTurnMeter.GetCount(sessionId);
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry finished: toolsInvokedAfterRetry={Count}",
                            sessionId, retryPassTools);
                    }
                    // 读类工具接地：判定依据同上（工具计数 + 用户原文），与推理流无关。
                    else if (firstPassTools == 0
                             && DocumentReadIntentHeuristic.LikelyRequiresDocumentReadTool(userMessage)
                             && funcsForRequired is { Count: > 0 })
                    {
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry (read): firstPassTools=0 documentReadIntent=true functionsForRequired={FnCount}",
                            sessionId, funcsForRequired.Count);
                        yield return new StreamItem(
                            IsWarning: true,
                            Content: "首轮响应未执行读类文档工具；正在自动续跑一轮…");

                        var retryHistoryRead = CloneChatHistory(turn.HistoryToUse);
                        retryHistoryRead.AddAssistantMessage(ReasoningTagStreamParser.StripReasoningTags(fullResponse.ToString()));
                        retryHistoryRead.AddUserMessage(ToolGroundingRetryMessages.ReadNudgeUserMessage);
                        fullResponse.Clear();
                        ToolInvocationTurnMeter.ResetCount(sessionId);

                        var maxTokRead = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
                        OpenAIPromptExecutionSettings retrySettingsRead = new()
                        {
                            MaxTokens = maxTokRead,
                            FunctionChoiceBehavior = FunctionChoiceBehavior.Required(funcsForRequired, autoInvoke: true)
                        };
                        CopyOptionalPromptSettings(turn.ExecSettings, retrySettingsRead);

                        var requiredApiFallbackRead = false;
                        var requiredPassBufferRead = new List<StreamItem>();
                        streamOutcome.ContextLengthRetryRequested = false;
                        try
                        {
                            await foreach (var streamItem in EnumerateMainModelStreamAsync(
                                               chat,
                                               retryHistoryRead,
                                               retrySettingsRead,
                                               kernel,
                                               sessionId,
                                               ctxConfig,
                                               state,
                                               fullResponse,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               dashScopeReasoningYieldedFromContext,
                                               ct).ConfigureAwait(false))
                            {
                                requiredPassBufferRead.Add(streamItem);
                            }
                        }
                        catch (Exception ex) when (IsLikelyToolChoiceOrFunctionCallApiError(ex))
                        {
                            _logger.LogWarning(ex, "[{SessionId}] tool_choice Required 读类重试失败，回退为 Auto + 强提示。", sessionId);
                            requiredApiFallbackRead = true;
                            requiredPassBufferRead.Clear();
                            fullResponse.Clear();
                        }

                        foreach (var streamItem in requiredPassBufferRead)
                            yield return streamItem;

                        if (requiredApiFallbackRead)
                        {
                            ToolInvocationTurnMeter.ResetCount(sessionId);
                            fullResponse.Clear();
                            var fallbackSettingsRead = new OpenAIPromptExecutionSettings
                            {
                                MaxTokens = maxTokRead,
                                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(funcsForRequired)
                            };
                            CopyOptionalPromptSettings(turn.ExecSettings, fallbackSettingsRead);
                            streamOutcome.ContextLengthRetryRequested = false;
                            await foreach (var streamItem in EnumerateMainModelStreamAsync(
                                               chat,
                                               retryHistoryRead,
                                               fallbackSettingsRead,
                                               kernel,
                                               sessionId,
                                               ctxConfig,
                                               state,
                                               fullResponse,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               dashScopeReasoningYieldedFromContext,
                                               ct).ConfigureAwait(false))
                            {
                                yield return streamItem;
                            }
                        }

                        var retryPassToolsRead = ToolInvocationTurnMeter.GetCount(sessionId);
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry (read) finished: toolsInvokedAfterRetry={Count}",
                            sessionId, retryPassToolsRead);
                    }

                    _logger.LogInformation(
                        "[{SessionId}] DashScope reasoning→StreamItem from context queue: count={Count} (若 SSE tap 里 reasoningParsed>0 而此处为 0，重点查 AsyncLocal/Drain 时序)",
                        sessionId, dashScopeReasoningYieldedFromContext.Count);
                }
                finally
                {
                    ToolInvocationTurnMeter.EndTurn(sessionId);
                }
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

    private sealed class StreamingPassOutcome
    {
        public bool ContextLengthRetryRequested;
    }

    private sealed class DashScopeReasoningCounter
    {
        public int Count;
    }

    private static ChatHistory CloneChatHistory(ChatHistory source)
    {
        var h = new ChatHistory();
        for (var i = 0; i < source.Count; i++)
            h.Add(source[i]);
        return h;
    }

    private static void CopyOptionalPromptSettings(OpenAIPromptExecutionSettings from, OpenAIPromptExecutionSettings to)
    {
        to.Temperature = from.Temperature;
        to.TopP = from.TopP;
        to.FrequencyPenalty = from.FrequencyPenalty;
        to.PresencePenalty = from.PresencePenalty;
        if (from.StopSequences is { Count: > 0 })
            to.StopSequences = from.StopSequences;
    }

    private static bool IsLikelyToolChoiceOrFunctionCallApiError(Exception ex)
    {
        var msg = ex.Message + "\n" + ex;
        return msg.Contains("tool_choice", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("tool choice", StringComparison.OrdinalIgnoreCase)
            || (msg.Contains("required", StringComparison.OrdinalIgnoreCase)
                && (msg.Contains("tools", StringComparison.OrdinalIgnoreCase) || msg.Contains("function", StringComparison.OrdinalIgnoreCase)));
    }

    private async IAsyncEnumerable<StreamItem> EnumerateMainModelStreamAsync(
        IChatCompletionService chat,
        ChatHistory history,
        OpenAIPromptExecutionSettings settings,
        Kernel kernel,
        string sessionId,
        ContextWindowConfig ctxConfig,
        SessionState state,
        StringBuilder fullResponse,
        StreamingPassOutcome outcome,
        int contextAttemptIndex,
        DashScopeReasoningCounter dashScopeCounter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var toolCallArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var streamEnum = chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct).GetAsyncEnumerator(ct);
        var allowContextRetry = contextAttemptIndex == 0 && !ctxConfig.PassThroughContext && ctxConfig.ContextLengthRetryEnabled;

        while (true)
        {
            bool moved;
            try
            {
                moved = await streamEnum.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (allowContextRetry && IsContextLengthError(ex))
            {
                _logger.LogWarning(ex, "[{SessionId}] Context length exceeded, retrying with halved budget.", sessionId);
                TrimHistoryForRetry(state.History, ctxConfig.ContextLengthRetryMaxTurns, ctxConfig);
                outcome.ContextLengthRetryRequested = true;
                yield break;
            }

            if (!moved)
                break;

            foreach (var reasoningDelta in DashScopeReasoningSessionBridge.DrainForSession(sessionId))
            {
                dashScopeCounter.Count++;
                yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);
            }

            foreach (var reasoningDelta in DashScopeReasoningContext.DrainCurrentFrame())
            {
                dashScopeCounter.Count++;
                yield return new StreamItem(IsWarning: false, Content: reasoningDelta, Kind: StreamSegmentKind.Reasoning);
            }

            var chunk = streamEnum.Current;
            var metaReasoning = OpenAiStreamingReasoningHelper.TryGetReasoningFromMetadata(chunk);
            if (!string.IsNullOrEmpty(metaReasoning))
                yield return new StreamItem(IsWarning: false, Content: metaReasoning, Kind: StreamSegmentKind.Reasoning);

            foreach (var toolDelta in StreamingToolCallDeltaHelper.ExtractFromChunk(chunk, toolCallArgBudget))
                yield return new StreamItem(IsWarning: false, Content: "", Kind: StreamSegmentKind.ToolCallDelta, ToolDelta: toolDelta);

            // fullResponse 仅累积 SK 正文的 chunk.Content；百炼 reasoning_content 仅经上方 Reasoning StreamItem 下发，不入此缓冲。
            if (chunk.Content is { Length: > 0 } text)
            {
                fullResponse.Append(text);
                yield return new StreamItem(IsWarning: false, Content: text);
            }
        }
    }

    /// <summary>将最旧若干轮（最多 6 轮）压缩为一段摘要并替换为一条消息；若配置了落盘目录则先将被压缩的原文追加写入会话历史文件。</summary>
    private async Task<(bool DidCompact, int MessagesRemoved, int SummaryLength)> TrySummarizeOldTurnsAsync(ChatHistory history, Kernel kernel, IChatCompletionService chatService, ContextWindowConfig ctx, string sessionId, CancellationToken ct)
    {
        var dir = GetConversationHistoryDirectory(ctx);
        var r = await ContextManager.SummarizeOldTurnsCoreAsync(history, kernel, chatService, ctx, sessionId, dir, ct).ConfigureAwait(false);
        if (r.DidCompact)
            _logger.LogDebug("[{SessionId}] Summarized {MessagesRemoved} messages into one block.", sessionId, r.MessagesRemoved);
        return r;
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
        var allFunctions = ClientTypeToolFilter.GetAllowedFunctions(kernel, clientType, sessionId);
        // 排除 run_subtask（防递归）和 compact_conversation（防子代理操作主会话历史）
        var allowedFunctions = allFunctions
            .Where(f => !string.Equals(f.Name, "run_subtask", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(f.Name, "compact_conversation", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (allowedFunctions.Count == 0)
            return "[错误] 当前端无可用的工具集，无法执行子任务。";

        var systemPrompt = "你是一个子代理。请完成用户给出的子任务，可使用现有工具。完成后仅用一段自然语言总结最终结果，不要逐步解释过程。"
            + " 用户最新表述优先于历史中的旧结论；本机/文档/网页的当前状态须用工具查询后再下结论，勿仅凭聊天记录推断。"
            + " 若子任务涉及修改本机文件或 Office 文档，须实际调用工具并依据工具返回再总结；不得未调用工具却声称已完成变更。";
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
        Task SendSubtaskToolDeltaAsync(ToolCallStreamDelta d) =>
            SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage
            {
                Type = "tool_call_delta",
                ToolCallId = d.CallId,
                ToolName = d.ToolName,
                ArgumentsDelta = string.IsNullOrEmpty(d.ArgumentsDelta) ? null : d.ArgumentsDelta,
                IsSubtask = true
            });
        try
        {
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage
            {
                Type = "subtask_start",
                TaskDescription = taskDescTrimmed,
                Constraints = string.IsNullOrWhiteSpace(constraints) ? null : constraints.Trim()
            }).ConfigureAwait(false);
            using (DashScopeCallKindContext.EnterBackground())
            {
                var useSkAgent = _configService.Current.SemanticKernel?.UseChatCompletionAgentForSubtask == true;
                if (useSkAgent)
                {
                    await _skSubtaskAgentRunner.RunStreamingAsync(
                        kernel,
                        string.IsNullOrWhiteSpace(_kernelAccessor.ActiveModelId) ? null : _kernelAccessor.ActiveModelId.Trim(),
                        systemPrompt,
                        userContent,
                        allowedFunctions,
                        async text =>
                        {
                            fullResponse.Append(text);
                            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_chunk", Content = text }).ConfigureAwait(false);
                        },
                        settings,
                        timeoutCts.Token,
                        onToolCallDeltaAsync: SendSubtaskToolDeltaAsync).ConfigureAwait(false);
                }
                else
                {
                    var subtaskToolArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);
                    await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(subHistory, settings, kernel, timeoutCts.Token).ConfigureAwait(false))
                    {
                        foreach (var td in StreamingToolCallDeltaHelper.ExtractFromChunk(chunk, subtaskToolArgBudget))
                            await SendSubtaskToolDeltaAsync(td).ConfigureAwait(false);
                        if (chunk.Content is { Length: > 0 } text)
                        {
                            fullResponse.Append(text);
                            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_chunk", Content = text }).ConfigureAwait(false);
                        }
                    }
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
        var (didCompact, messagesRemoved, _) = await ContextManager.SummarizeOldTurnsCoreAsync(state.History, kernel, chat, ctx, sessionId, dir, ct).ConfigureAwait(false);
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
        string? clientType,
        string? sessionId)
    {
        IReadOnlyList<KernelFunction>? result = null;
        if (selectedPairs is { Count: > 0 })
        {
            var filtered = ClientTypeToolFilter.Filter(selectedPairs, clientType, sessionId);
            if (filtered.Count > 0)
                result = GetFunctionsByPluginAndFunctionNames(kernel, filtered);
        }
        if (result == null)
            result = ClientTypeToolFilter.GetAllowedFunctions(kernel, clientType, sessionId);

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
    /// 注入：最新用户意图优先；可验证事实须本轮用工具刷新，不以聊天历史替代（与 Memory/AccurateData 边界见文内）。
    /// </summary>
    private const string LatestIntentAndGroundedFactsInstruction =
        "[意图优先级] 当用户在后续消息中纠正、细化或推翻先前要求时，以最近一条用户消息中的意图为准；"
        + "若与早前 assistant 总结或旧结论冲突，以最新用户表述为准。"
        + "未冲突的上下文约束（如已约定的路径、端侧、任务目标）仍应保留。"
        + "\n\n[事实与可验证状态] 关于会变化或需实勘的信息"
        + "（本机文件/目录列表与是否存在、run_command 能反映的本机状态、当前网页或当前文档的实时内容等），"
        + "禁止仅凭对话历史或旧 assistant 回复断言；须在本轮通过相应工具"
        + "（如 run_command、File/浏览器/Office 侧读取工具等）获取结果后再作答。"
        + "用户明确要求「再看一下」「重新确认」「现在有什么」等时，必须先执行能反映真实状态的查询。"
        + "通过 Memory、AccurateData 等工具写入或检索的记忆与结构化数据仍可使用，但不可替代「当前目录列表」等须当场核实的状态。";

    /// <summary>
    /// 注入：用户界面看不到工具原始返回全文，模型必须在最终回复中整理复述。
    /// </summary>
    private const string ToolResultEchoSystemInstruction =
        "[工具与回答方式] 用户对话界面中看不到工具的原始返回全文（执行过程里可能仅有简短摘要）。"
        + "在调用工具前，可用一句简短说明本轮目标（便于用户理解你的意图）。"
        + "凡你调用了工具并从工具结果中获得了对用户有用的文字或数据，在本轮最终回复里必须用自然语言完整整理并复述给用户；"
        + "禁止仅用「已读取」「已完成」等占位描述而不给出实质内容。";

    /// <summary>
    /// 构建本轮流式请求用的 ChatHistory：可选追加 client 身份后缀；再追加意图/事实约束与工具结果复述约束。
    /// </summary>
    private static ChatHistory BuildHistoryForStreamingTurn(ChatHistory stateHistory, string? identitySuffix)
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

        if (historyToUse.Count > 0 && historyToUse[0].Role == AuthorRole.System)
        {
            var sys = historyToUse[0].Content ?? "";
            var augmented = sys + "\n\n" + LatestIntentAndGroundedFactsInstruction + "\n\n" + ToolResultEchoSystemInstruction;
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
            return "你是浏览器侧边栏助手：可对「本机路径上的」Word/Excel/PPT 使用 Word/Excel/Ppt 插件读写（与任务窗格互补；本端无 CurrentDocument 当前文档 API，须用文件路径调用文档工具）。另支持网页截图与页面脚本、附件与文件工具、命令行等。用户已提供 docx/xlsx 等路径时，应直接用相应文档工具完成编辑与批注，不得以「浏览器端不能改 Word」为由拒绝或要求用户必须切到任务窗格。";
        if (string.Equals(ct, "office-word", StringComparison.OrdinalIgnoreCase))
            return "你是 Word 侧助手，负责当前打开的 Word 文档；网页相关操作请由用户在浏览器侧边栏端完成。你只负责本端能力；若需求属于另一客户端，请说明并建议用户切换。";
        if (string.Equals(ct, "office-excel", StringComparison.OrdinalIgnoreCase))
            return "你是 Excel 侧助手，负责当前打开的 Excel 工作簿；网页相关操作请由用户在浏览器侧边栏端完成。你只负责本端能力；若需求属于另一客户端，请说明并建议用户切换。";
        if (string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase))
            return "你是 WPS 侧助手，负责当前打开的 WPS 文档；网页相关操作请由用户在浏览器侧边栏端完成。你只负责本端能力；若需求属于另一客户端，请说明并建议用户切换。";
        if (string.Equals(ct, "office-powerpoint", StringComparison.OrdinalIgnoreCase))
            return "你是 PowerPoint 侧助手，负责当前打开的 PowerPoint 演示文稿；网页相关操作请由用户在浏览器侧边栏端完成。你只负责本端能力；若需求属于另一客户端，请说明并建议用户切换。";
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
}
