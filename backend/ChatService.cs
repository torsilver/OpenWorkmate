using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Plugins;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Memory;
using OfficeCopilot.Server.Services.Plan;
using OfficeCopilot.Server.Services.CrossAgentTask;
using OfficeCopilot.Server.Services.ScheduledTask;
using OfficeCopilot.Server.Services.ContextProviders;
using OfficeCopilot.Server.Services.DashScope;
using OfficeCopilot.Server.Mcp;
using OfficeCopilot.Server.Services.Chat;
using OfficeCopilot.Server.Services.Maf;

namespace OfficeCopilot.Server;

public sealed partial class ChatService : IDisposable
{
    /// <summary>当前选中的模型 Id，用于按 key 解析 IChatClient。</summary>
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
    private readonly IChatRuntimeAccessor _runtime;
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly IPlanStore _planStore;
    private readonly AgentDebugStatsService _agentDebugStats;
    private readonly object _runtimeLock = new();

    public ChatService(IConfiguration config, ILogger<ChatService> logger, ILoggerFactory loggerFactory, ConfigService configService, SkillService skillService, McpClientManager mcpManager, IToolSelector toolSelector, IToolIndexService toolIndex, IVectorStore vectorStore, IServiceProvider serviceProvider, IChatRuntimeAccessor runtimeAccessor, EmbeddingProvider embeddingProvider, IPlanStore planStore, AgentDebugStatsService agentDebugStats)
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
        _runtime = runtimeAccessor;
        _embeddingProvider = embeddingProvider;
        _planStore = planStore;
        _agentDebugStats = agentDebugStats;

        var session = configService.Current.Session ?? new SessionConfig();
        var cleanupInterval = session.CleanupIntervalMinutes;

        _configService.OnConfigChanged += () => _ = RebuildRuntimeAsync(skipUserToolIndexSync: false);
        _skillService.OnSkillsChanged += () => _ = RebuildRuntimeAsync(skipUserToolIndexSync: false);

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

    /// <summary>获取要注册的模型列表：仅 <see cref="AppConfig.AiModels"/> 中 Enabled 且含 Id 的项（旧 <c>ai</c> 已在加载/保存时迁入此列表）。</summary>
    private IReadOnlyList<AiModelEntry> GetModelEntriesToRegister()
    {
        var config = _configService.Current;
        if (config.AiModels == null || config.AiModels.Count == 0)
            return Array.Empty<AiModelEntry>();
        return config.AiModels.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Id)).ToList();
    }

    /// <summary>获取当前应使用的模型配置（用于 system prompt、日志）；无模型条目时返回 null。</summary>
    private AiModelEntry? GetActiveModelEntry()
    {
        var config = _configService.Current;
        if (config.AiModels == null || config.AiModels.Count == 0)
            return null;
        var activeId = (config.ActiveModelId ?? "").Trim();
        var entry = config.AiModels.FirstOrDefault(e => string.Equals(e.Id, activeId, StringComparison.OrdinalIgnoreCase));
        if (entry != null) return entry;
        return config.AiModels.FirstOrDefault(e => e.Enabled) ?? config.AiModels.FirstOrDefault();
    }

    /// <summary>重建 Kernel（内置插件 + 用户 Skill + MCP）；可由 Program 在 --build-tool-index 模式下调用。</summary>
    /// <param name="skipUserToolIndexSync">为 true 时不调度用户工具向量增量同步（用于进程首次启动与预构建中间步骤）。</param>
    public async Task RebuildRuntimeAsync(bool skipUserToolIndexSync = false)
    {
        var config = _configService.Current;
        var entries = GetModelEntriesToRegister();
        var chatClients = new Dictionary<string, IChatClient>(StringComparer.OrdinalIgnoreCase);

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

                chatClients[entry.Id] = CreateDirectChatClient(entry.Id, modelId, apiKey, endpointUri);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skip model {Id} ({Provider}): registration failed.", entry.Id, entry.Provider);
            }
        }

        // 嵌入服务：MEAI IEmbeddingGenerator
        IEmbeddingGenerator<string, Embedding<float>>? embGenerator = null;
        var activeEmb = _configService.GetActiveEmbeddingEntry();
        if (activeEmb != null && string.Equals((activeEmb.Source ?? "").Trim(), "Remote", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(activeEmb.ModelId))
        {
            var embModelId = (activeEmb.ModelId ?? "").Trim();
            var embApiKey = (activeEmb.ApiKey ?? "").Trim();
            var embEndpoint = (activeEmb.Endpoint ?? "").Trim();
            Uri? embUri = null;
            if (embEndpoint.Length > 0 && Uri.TryCreate(embEndpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                embUri = u;
            try
            {
                if (embUri != null)
                {
                    embGenerator = CreateDirectEmbeddingGenerator(embModelId, embApiKey, embUri);
                    _logger.LogInformation("Embedding registered (MEAI): Id={EmbId}, Endpoint={Endpoint}", activeEmb.Id, embUri.ToString());
                }
                else
                {
                    _logger.LogWarning("Embedding 未配置 Endpoint，已跳过注册（避免误用默认 OpenAI 地址）。请在设置中为该条填写 Endpoint 并保存。Id={EmbId}", activeEmb.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skip embedding registration (Remote): {Message}", ex.Message);
            }
        }
        _embeddingProvider.SetGenerator(embGenerator);

        var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();

        // 已停用的内置插件 ID（不区分大小写）
        var disabledBuiltIn = _configService.Current.DisabledBuiltInPlugins?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .ToHashSet() ?? new HashSet<string>();

        var pluginInstances = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        void RegisterPlugin(object instance, string name) => pluginInstances[name] = instance;

        if (!disabledBuiltIn.Contains("cli"))
            RegisterPlugin(new CliPlugin(), "CLI");
        if (!disabledBuiltIn.Contains("excel"))
            RegisterPlugin(new ExcelPlugin(), "Excel");
        if (!disabledBuiltIn.Contains("word"))
        {
            var wordPluginLogger = _loggerFactory.CreateLogger<WordPlugin>();
            RegisterPlugin(new WordPlugin(wordPluginLogger), "Word");
        }
        if (!disabledBuiltIn.Contains("ppt"))
            RegisterPlugin(new PptPlugin(_loggerFactory.CreateLogger<PptPlugin>()), "Ppt");

        var rpcManager = _serviceProvider.GetRequiredService<RpcManager>();
        var screenshotCache = _serviceProvider.GetRequiredService<ScreenshotCacheService>();
        var attachmentCache = _serviceProvider.GetRequiredService<AttachmentCacheService>();
        var browserPluginLogger = _loggerFactory.CreateLogger<BrowserPlugin>();
        var filePluginLogger = _loggerFactory.CreateLogger<FilePlugin>();
        if (!disabledBuiltIn.Contains("browser"))
            RegisterPlugin(new BrowserPlugin(sessionManager, rpcManager, screenshotCache, browserPluginLogger), "Browser");
        if (!disabledBuiltIn.Contains("file"))
            RegisterPlugin(new FilePlugin(screenshotCache, attachmentCache, filePluginLogger), "File");
        if (!disabledBuiltIn.Contains("system"))
            RegisterPlugin(new SystemPlugin(), "System");
        if (!disabledBuiltIn.Contains("mcp_stt"))
        {
            var transcribeService = _serviceProvider.GetRequiredService<ITranscribeService>();
            var sttPluginLogger = _loggerFactory.CreateLogger<SttPlugin>();
            RegisterPlugin(new SttPlugin(transcribeService, sttPluginLogger), "MCP_STT");
        }
        if (!disabledBuiltIn.Contains("mcp_ocr"))
        {
            var ocrService = _serviceProvider.GetRequiredService<IOcrService>();
            var ocrPluginLogger = _loggerFactory.CreateLogger<OcrPlugin>();
            RegisterPlugin(new OcrPlugin(ocrService, ocrPluginLogger), "MCP_OCR");
        }
        if (!disabledBuiltIn.Contains("pdf"))
        {
            var pdfPluginLogger = _loggerFactory.CreateLogger<PdfPlugin>();
            RegisterPlugin(new PdfPlugin(pdfPluginLogger), "Pdf");
        }

        if (!disabledBuiltIn.Contains("currentdocument"))
        {
            var currentDocLogger = _loggerFactory.CreateLogger<CurrentDocumentPlugin>();
            RegisterPlugin(new CurrentDocumentPlugin(sessionManager, rpcManager, currentDocLogger), "CurrentDocument");
        }

        var clawhubRunner = _serviceProvider.GetRequiredService<ClawhubScriptRunner>();
        if (!disabledBuiltIn.Contains("clawhub"))
            RegisterPlugin(new ClawhubSkillPlugin(_skillService, clawhubRunner, _configService, _loggerFactory.CreateLogger<ClawhubSkillPlugin>()), "ClawhubSkill");

        // 阶段 3：记忆插件（仅当已配置 Embedding 时注册）
        if (_embeddingProvider.IsConfigured && !disabledBuiltIn.Contains("memory"))
        {
            var memorySvc = _serviceProvider.GetRequiredService<IMemoryStoreService>();
            RegisterPlugin(new MemoryPlugin(memorySvc, sessionManager, _loggerFactory.CreateLogger<MemoryPlugin>()), "Memory");
        }

        if (!disabledBuiltIn.Contains("context"))
            RegisterPlugin(new CompactConversationPlugin(this), "Context");

        if (!disabledBuiltIn.Contains("subagent"))
            RegisterPlugin(new SubagentPlugin(this), "Subagent");

        if (!disabledBuiltIn.Contains("crossagenttask"))
        {
            var taskStore = _serviceProvider.GetRequiredService<ICrossAgentTaskStore>();
            RegisterPlugin(new CrossAgentTaskPlugin(taskStore, sessionManager, _loggerFactory.CreateLogger<CrossAgentTaskPlugin>()), "CrossAgentTask");
        }

        if (!disabledBuiltIn.Contains("plan"))
        {
            var planPlugin = _serviceProvider.GetRequiredService<PlanPlugin>();
            RegisterPlugin(planPlugin, "Plan");
        }

        if (!disabledBuiltIn.Contains("skillauthor"))
        {
            var skillAuthorPlugin = _serviceProvider.GetRequiredService<SkillAuthorPlugin>();
            RegisterPlugin(skillAuthorPlugin, "SkillAuthor");
        }

        if (!disabledBuiltIn.Contains("user_options"))
        {
            var userOptionsManager = _serviceProvider.GetRequiredService<UserOptionsManager>();
            var userOptionsLogger = _loggerFactory.CreateLogger<UserOptionsPlugin>();
            RegisterPlugin(new UserOptionsPlugin(userOptionsManager, userOptionsLogger), "UserOptions");
        }

        if (!disabledBuiltIn.Contains("accuratedata"))
            RegisterPlugin(new AccurateDataPlugin(_configService), "AccurateData");

        if (!disabledBuiltIn.Contains("meetingtranscript"))
        {
            var meetingStore = _serviceProvider.GetRequiredService<IMeetingTranscriptStore>();
            RegisterPlugin(new MeetingTranscriptPlugin(meetingStore), "MeetingTranscript");
        }

        if (!disabledBuiltIn.Contains("scheduledtask"))
        {
            var scheduledTaskStore = _serviceProvider.GetRequiredService<IScheduledTaskStore>();
            RegisterPlugin(new ScheduledTaskPlugin(scheduledTaskStore), "ScheduledTask");
        }

        // 动态注册 UserSkill（prompt-based skills 作为简单的 AIFunction 注册到 ToolRegistry）
        var toolRegistry = new ToolRegistry();
        foreach (var (name, instance) in pluginInstances)
            toolRegistry.RegisterPluginFromObject(instance, name);

        var userSkills = _skillService.GetAllSkills();
        var skillCount = 0;
        foreach (var skill in userSkills)
        {
            if (!skill.Enabled || string.IsNullOrWhiteSpace(skill.PromptTemplate)) continue;
            try
            {
                var safeName = SanitizeSkillFunctionName(skill.Id);
                var pluginName = $"UserSkill_{safeName}";
                var template = skill.PromptTemplate;
                var desc = skill.Description;
                var fn = AIFunctionFactory.Create(
                    async (string input, CancellationToken ct2) =>
                    {
                        var client = _runtime.GetChatClient();
                        if (client == null) return "[错误] IChatClient 未就绪。";
                        var rendered = template.Replace("{{$input}}", input).Replace("{{input}}", input);
                        var messages = new List<ChatMessage> { new(ChatRole.User, rendered) };
                        var opts = new ChatOptions { MaxOutputTokens = 4000, Temperature = 0.1f };
                        var response = await client.GetResponseAsync(messages, opts, ct2).ConfigureAwait(false);
                        return response.Text ?? "";
                    },
                    new AIFunctionFactoryOptions { Name = safeName, Description = desc ?? "" });
                toolRegistry.Register(pluginName, safeName, fn);
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
            if (!mcpConfig.Enabled) continue;
            try
            {
                var client = await _mcpManager.StartClientAsync(mcpConfig, envOverlay: null);
                var wrapper = new McpKernelPlugin(client, $"MCP_{mcpConfig.Name}", _loggerFactory.CreateLogger<McpKernelPlugin>());
                var mcpTools = await wrapper.BuildMcpAIToolsAsync();
                toolRegistry.RegisterPlugin($"MCP_{mcpConfig.Name}", mcpTools);
                mcpCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bind MCP server {Name}", mcpConfig.Name);
            }
        }
        
        var activeEntry = GetActiveModelEntry();
        var activeId = config.AiModels != null && config.AiModels.Count > 0
            ? (config.ActiveModelId ?? "").Trim()
            : "default";
        if (string.IsNullOrEmpty(activeId) || entries.All(e => !string.Equals(e.Id, activeId, StringComparison.OrdinalIgnoreCase)))
            activeId = entries.Count > 0 ? entries[0].Id : "default";

        lock (_runtimeLock)
        {
            _activeModelId = activeId;
            _runtime.SetActiveModelId(_activeModelId);
            _runtime.SetChatClients(chatClients);
            _runtime.SetToolRegistry(toolRegistry);
        }

        _logger.LogInformation("Runtime rebuilt. ActiveModel: {ActiveId}, Plugins: {PluginCount}, UserSkills: {SkillCount}, MCPs: {McpCount}, ToolRegistry: {ToolCount}",
            _activeModelId, pluginInstances.Count, skillCount, mcpCount, toolRegistry.GetAllTools().Count);

        if (!skipUserToolIndexSync)
            _ = SyncUserToolIndexInBackgroundAsync(toolRegistry);
    }

    /// <summary>从 OpenAI SDK 创建 MEAI <see cref="IChatClient"/>（经 <c>Microsoft.Extensions.AI.OpenAI</c> 适配），不经过 SK。</summary>
    private IChatClient CreateDirectChatClient(string entryId, string modelId, string apiKey, Uri? endpointUri)
    {
        var logHandler = new OpenAiLoggingHandler(_loggerFactory.CreateLogger<OpenAiLoggingHandler>());
        var dashHandler = new DashScopeOpenAiCompatHandler(
            _configService, entryId, logHandler,
            _loggerFactory.CreateLogger<DashScopeOpenAiCompatHandler>());
        var httpClient = new HttpClient(dashHandler);
        var options = new OpenAI.OpenAIClientOptions();
        if (endpointUri != null) options.Endpoint = endpointUri;
        options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient);
        var credential = new System.ClientModel.ApiKeyCredential(
            string.IsNullOrEmpty(apiKey) ? "placeholder" : apiKey);
        var openAiClient = new OpenAI.OpenAIClient(credential, options);
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }

    /// <summary>从 OpenAI SDK 创建 MEAI <see cref="IEmbeddingGenerator{String, Embedding}"/>（经 <c>Microsoft.Extensions.AI.OpenAI</c> 适配），不经过 SK。</summary>
    private IEmbeddingGenerator<string, Embedding<float>> CreateDirectEmbeddingGenerator(string modelId, string apiKey, Uri endpointUri)
    {
        var logHandler = new OpenAiLoggingHandler(_loggerFactory.CreateLogger<OpenAiLoggingHandler>());
        var httpClient = new HttpClient(logHandler);
        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = endpointUri,
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };
        var credential = new System.ClientModel.ApiKeyCredential(
            string.IsNullOrEmpty(apiKey) ? "placeholder" : apiKey);
        var openAiClient = new OpenAI.OpenAIClient(credential, options);
        return openAiClient.GetEmbeddingClient(modelId).AsIEmbeddingGenerator(null);
    }

    /// <summary>后台增量同步用户工具索引（配置/技能变更后）；不阻塞请求。</summary>
    private async Task SyncUserToolIndexInBackgroundAsync(ToolRegistry toolRegistry)
    {
        try
        {
            await _toolIndex.SyncUserToolIndexAsync(toolRegistry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "User tool index sync failed.");
        }
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
            : AiEmbeddedDefaults.DefaultSystemPrompt.Trim();
        var guidance = BuiltinTaskPluginSystemGuidance.Trim();
        if (string.IsNullOrEmpty(basePrompt))
            return guidance;
        return basePrompt + "\n\n" + guidance;
    }

    public List<ChatMessage> GetSessionHistory(string sessionId)
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
        string systemPrompt;

        lock (_runtimeLock)
        {
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
                var items = new List<AIContent> { new Microsoft.Extensions.AI.TextContent(userMessage) };
                foreach (var att in attachments)
                {
                    if (string.IsNullOrWhiteSpace(att.Data)) continue;
                    var mime = string.IsNullOrWhiteSpace(att.MimeType) ? "image/png" : att.MimeType;
                    items.Add(new DataContent(Convert.FromBase64String(att.Data.Trim()), mime));
                }
                state.History.Add(new ChatMessage(ChatRole.User, items));
            }
            else
            {
                state.History.Add(new ChatMessage(ChatRole.User, userMessage));
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
                SessionManager = sessionManagerForStatus,
                CtxConfig = ctxConfig
            };

            var skFeat = _configService.Current.SemanticKernel;

            var workflow = Services.Chat.Executors.ChatTurnWorkflow.Build(_serviceProvider);
            await Services.Chat.Executors.ChatTurnWorkflow.RunAsync(workflow, turn, ct).ConfigureAwait(false);

            foreach (var w in turn.ContextWarnings)
                yield return new StreamItem(IsWarning: true, Content: w);
            turn.ContextWarnings.Clear();

            var contextProviders = BuildContextProviders(turn, sessionManagerForStatus);

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
                    await foreach (var streamItem in MafAgentGroupChatSessionRunner.InvokeStreamingAsync(
                                       _runtime, _loggerFactory, turn.HistoryToUse, turn.ExecSettings, turn.SessionManager, sessionId, ct)
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
                    try
                    {
                        var cc = _runtime.GetChatClient();
                        if (cc != null)
                        {
                            var hostPreambleOpts = new ChatClientAgentOptions
                            {
                                ChatOptions = new ChatOptions
                                {
                                    MaxOutputTokens = 256,
                                    Temperature = 0.3f,
                                    Instructions = "用一两句话说明你将如何组织回答（不写正文解答）。",
                                },
                            };
                            var hostBriefAgent = new ChatClientAgent(cc, hostPreambleOpts, _loggerFactory, _runtime.GetPluginServices() ?? _serviceProvider);
                            var preambleSession = await hostBriefAgent.CreateSessionAsync(ct).ConfigureAwait(false);
                            var preambleMsgs = new List<ChatMessage> { new(ChatRole.User, userMessage) };
                            var preambleRun = new ChatClientAgentRunOptions(hostPreambleOpts.ChatOptions ?? new ChatOptions());
                            await foreach (var u in hostBriefAgent.RunStreamingAsync(preambleMsgs, preambleSession, preambleRun, ct).ConfigureAwait(false))
                            {
                                if (u.Text is { Length: > 0 } t)
                                    preambleSb.Append(t);
                            }
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

                ToolInvocationTurnMeter.BeginTurn(sessionId);
                try
                {
                    var streamOutcome = new StreamPassOutcome();
                    var useMafMain = _runtime.GetChatClient() is not null;
                    if (!useMafMain)
                    {
                        const string errMsg =
                            "[错误] 主会话需要 MEAI IChatClient（MAF）。请确认模型已配置且 Kernel 重建成功；已不再使用 Semantic Kernel 流式路径。";
                        yield return new StreamItem(IsWarning: true, Content: errMsg);
                        fullResponse.Append(errMsg);
                    }
                    else
                    {
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        if (attempt > 0)
                            fullResponse.Clear();

                        streamOutcome.ContextLengthRetryRequested = false;
                        await foreach (var streamItem in MafMainSessionStreamRunner.EnumerateStreamingAsync(
                                           _runtime.GetChatClient()!,
                                           _runtime,
                                           _loggerFactory,
                                           _serviceProvider,
                                           turn.HistoryToUse,
                                           turn.ExecSettings,
                                           sessionManagerForStatus,
                                           sessionId,
                                           state,
                                           ctxConfig,
                                           streamOutcome,
                                           attempt,
                                           requireToolInvocation: false,
                                           contextProviders: contextProviders,
                                           ct).ConfigureAwait(false))
                        {
                            if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                                fullResponse.Append(streamItem.Content);
                            yield return streamItem;
                        }

                        if (streamOutcome.ContextLengthRetryRequested)
                        {
                            turn.HistoryToUse = BuildHistoryForStreamingTurn(state.History, turn.IdentitySuffix, turn.EnableSearchSuppressionSuffix);
                            continue;
                        }

                        break;
                    }

                    // 工具接地重试：依据「真实执行过的 Kernel 工具数」+ 用户原文启发式 + 可用函数列表。
                    // 百炼 thinking 走 reasoning_chunk，不入 fullResponse；不得用推理条数/正文参与判定（harness-engineering）。
                    // stream_warning 仅在首轮已有「可见助手正文」（fullResponse strip 后非空）却未调工具时下发；仅 thinking、无正文则静默续跑。
                    var firstPassTools = ToolInvocationTurnMeter.GetCount(sessionId);
                    var visibleAssistantFirstPass = ReasoningTagStreamParser.StripReasoningTags(fullResponse.ToString()).Trim();
                    var firstPassHadVisibleAssistantText = visibleAssistantFirstPass.Length > 0;
                    var clientTypeForTools = sessionManagerForStatus.GetClientType(sessionId);
                    IReadOnlyList<AITool> toolsForRequired = turn.SelectedTools is { Count: > 0 }
                        ? turn.SelectedTools
                        : _runtime.GetAllowedTools(clientTypeForTools, sessionId);
                    var needToolGroundingRetry = firstPassTools == 0
                        && MutationIntentHeuristic.LikelyRequiresLocalMutationTool(userMessage)
                        && toolsForRequired is { Count: > 0 };

                    if (needToolGroundingRetry)
                    {
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry: firstPassTools=0 mutationIntent=true functionsForRequired={FnCount} streamWarning={StreamWarning}",
                            sessionId, toolsForRequired.Count, firstPassHadVisibleAssistantText);
                        if (firstPassHadVisibleAssistantText)
                        {
                            yield return new StreamItem(
                                IsWarning: true,
                                Content: "首轮响应未触发本机文件类工具调用；正在自动续跑一轮以完成操作…");
                        }

                        var retryHistory = CloneHistory(turn.HistoryToUse);
                        retryHistory.Add(new ChatMessage(ChatRole.Assistant, visibleAssistantFirstPass));
                        retryHistory.Add(new ChatMessage(ChatRole.User, ToolGroundingRetryMessages.NudgeUserMessage));
                        fullResponse.Clear();
                        ToolInvocationTurnMeter.ResetCount(sessionId);

                        var maxTok = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
                        var retrySettings = new ChatOptions
                        {
                            MaxOutputTokens = maxTok,
                        };
                        CopyOptionalPromptSettings(turn.ExecSettings, retrySettings);

                        var requiredApiFallback = false;
                        var requiredPassBuffer = new List<StreamItem>();
                        streamOutcome.ContextLengthRetryRequested = false;
                        try
                        {
                            await foreach (var streamItem in MafMainSessionStreamRunner.EnumerateStreamingAsync(
                                               _runtime.GetChatClient()!,
                                               _runtime,
                                               _loggerFactory,
                                               _serviceProvider,
                                               retryHistory,
                                               retrySettings,
                                               sessionManagerForStatus,
                                               sessionId,
                                               state,
                                               ctxConfig,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               requireToolInvocation: true,
                                               ct: ct).ConfigureAwait(false))
                            {
                                if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                                    fullResponse.Append(streamItem.Content);
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
                            var fallbackSettings = new ChatOptions
                            {
                                MaxOutputTokens = maxTok,
                            };
                            CopyOptionalPromptSettings(turn.ExecSettings, fallbackSettings);
                            streamOutcome.ContextLengthRetryRequested = false;
                            await foreach (var streamItem in MafMainSessionStreamRunner.EnumerateStreamingAsync(
                                               _runtime.GetChatClient()!,
                                               _runtime,
                                               _loggerFactory,
                                               _serviceProvider,
                                               retryHistory,
                                               fallbackSettings,
                                               sessionManagerForStatus,
                                               sessionId,
                                               state,
                                               ctxConfig,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               requireToolInvocation: false,
                                               ct: ct).ConfigureAwait(false))
                            {
                                if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                                    fullResponse.Append(streamItem.Content);
                                yield return streamItem;
                            }
                        }

                        var retryPassTools = ToolInvocationTurnMeter.GetCount(sessionId);
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry finished: toolsInvokedAfterRetry={Count}",
                            sessionId, retryPassTools);
                    }
                    // 读类工具接地：判定依据同上；stream_warning 规则与写类一致（须首轮已有可见正文）。
                    else if (firstPassTools == 0
                             && DocumentReadIntentHeuristic.LikelyRequiresDocumentReadTool(userMessage)
                             && toolsForRequired is { Count: > 0 })
                    {
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry (read): firstPassTools=0 documentReadIntent=true functionsForRequired={FnCount} streamWarning={StreamWarning}",
                            sessionId, toolsForRequired.Count, firstPassHadVisibleAssistantText);
                        if (firstPassHadVisibleAssistantText)
                        {
                            yield return new StreamItem(
                                IsWarning: true,
                                Content: "首轮响应未执行读类文档工具；正在自动续跑一轮…");
                        }

                        var retryHistoryRead = CloneHistory(turn.HistoryToUse);
                        retryHistoryRead.Add(new ChatMessage(ChatRole.Assistant, visibleAssistantFirstPass));
                        retryHistoryRead.Add(new ChatMessage(ChatRole.User, ToolGroundingRetryMessages.ReadNudgeUserMessage));
                        fullResponse.Clear();
                        ToolInvocationTurnMeter.ResetCount(sessionId);

                        var maxTokRead = Math.Clamp(ctxConfig.ReservedOutputTokens, 256, 16_384);
                        var retrySettingsRead = new ChatOptions
                        {
                            MaxOutputTokens = maxTokRead,
                        };
                        CopyOptionalPromptSettings(turn.ExecSettings, retrySettingsRead);

                        var requiredApiFallbackRead = false;
                        var requiredPassBufferRead = new List<StreamItem>();
                        streamOutcome.ContextLengthRetryRequested = false;
                        try
                        {
                            await foreach (var streamItem in MafMainSessionStreamRunner.EnumerateStreamingAsync(
                                               _runtime.GetChatClient()!,
                                               _runtime,
                                               _loggerFactory,
                                               _serviceProvider,
                                               retryHistoryRead,
                                               retrySettingsRead,
                                               sessionManagerForStatus,
                                               sessionId,
                                               state,
                                               ctxConfig,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               requireToolInvocation: true,
                                               ct: ct).ConfigureAwait(false))
                            {
                                if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                                    fullResponse.Append(streamItem.Content);
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
                            var fallbackSettingsRead = new ChatOptions
                            {
                                MaxOutputTokens = maxTokRead,
                            };
                            CopyOptionalPromptSettings(turn.ExecSettings, fallbackSettingsRead);
                            streamOutcome.ContextLengthRetryRequested = false;
                            await foreach (var streamItem in MafMainSessionStreamRunner.EnumerateStreamingAsync(
                                               _runtime.GetChatClient()!,
                                               _runtime,
                                               _loggerFactory,
                                               _serviceProvider,
                                               retryHistoryRead,
                                               fallbackSettingsRead,
                                               sessionManagerForStatus,
                                               sessionId,
                                               state,
                                               ctxConfig,
                                               streamOutcome,
                                               contextAttemptIndex: 0,
                                               requireToolInvocation: false,
                                               ct: ct).ConfigureAwait(false))
                            {
                                if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                                    fullResponse.Append(streamItem.Content);
                                yield return streamItem;
                            }
                        }

                        var retryPassToolsRead = ToolInvocationTurnMeter.GetCount(sessionId);
                        _logger.LogInformation(
                            "[{SessionId}] Tool grounding retry (read) finished: toolsInvokedAfterRetry={Count}",
                            sessionId, retryPassToolsRead);
                    }

                    _logger.LogInformation(
                        "[{SessionId}] MAF 主会话流结束（百炼 reasoning 经 MafMainSessionStreamRunner / DashScope* 桥；若 SSE tap 与 Drain 不一致请查 AsyncLocal）",
                        sessionId);
                    }
                }
                finally
                {
                    ToolInvocationTurnMeter.EndTurn(sessionId);
                }
            }

            var assistantText = ReasoningTagStreamParser.StripReasoningTags(fullResponse.ToString());
            state.History.Add(new ChatMessage(ChatRole.Assistant, assistantText));
            var previewLen = Math.Min(200, assistantText.Length);
            var preview = previewLen > 0 ? assistantText.AsSpan(0, previewLen).ToString().Replace('\r', ' ').Replace('\n', ' ') : "";
            if (assistantText.Length > previewLen) preview += "…";
            _logger.LogInformation("[{SessionId}] Turn completed, turns={Turns}, assistantChars={AssistantChars}, assistantPreview={Preview}",
                sessionId, state.History.Count, assistantText.Length, preview);
        }
        finally { }
    }

    private static List<ChatMessage> CloneHistory(List<ChatMessage> source)
    {
        var h = new List<ChatMessage>(source.Count);
        for (var i = 0; i < source.Count; i++)
            h.Add(source[i]);
        return h;
    }

    private static void CopyOptionalPromptSettings(ChatOptions from, ChatOptions to)
    {
        to.Temperature = from.Temperature;
        to.TopP = from.TopP;
        to.FrequencyPenalty = from.FrequencyPenalty;
        to.PresencePenalty = from.PresencePenalty;
        if (from.StopSequences is not null && from.StopSequences.Count > 0)
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


    /// <summary>供 run_subtask 工具调用：在隔离的上下文中执行子任务，仅将最终自然语言结果返回给主 Agent，不把子任务内的多轮 tool 调用塞入主会话历史。</summary>
    public async Task<string> RunSubtaskAsync(string sessionId, string taskDescription, string? constraints, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "[错误] 无当前会话，无法执行子任务。";
        if (string.IsNullOrWhiteSpace(taskDescription))
            return "[错误] 子任务描述不能为空。";
        var taskDescTrimmed = taskDescription.Trim();
        var chatClient = _runtime.GetChatClient();
        if (chatClient == null)
            return "[错误] IChatClient 未就绪。";
        var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
        var clientType = sessionManager.GetClientType(sessionId);
        var allTools = _runtime.GetAllowedTools(clientType, sessionId);
        var allowedTools = allTools
            .Where(t => !string.Equals(t.Name, "run_subtask", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(t.Name, "compact_conversation", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (allowedTools.Count == 0)
            return "[错误] 当前端无可用的工具集，无法执行子任务。";

        var systemPrompt = "你是一个子代理。请完成用户给出的子任务，可使用现有工具。完成后仅用一段自然语言总结最终结果，不要逐步解释过程。"
            + " 用户最新表述优先于历史中的旧结论；本机/文档/网页的当前状态须用工具查询后再下结论，勿仅凭聊天记录推断。"
            + " 若子任务涉及修改本机文件或 Office 文档，须实际调用工具并依据工具返回再总结；不得未调用工具却声称已完成变更。";
        var userContent = taskDescTrimmed;
        if (!string.IsNullOrWhiteSpace(constraints))
            userContent += "\n\n约束：" + constraints.Trim();
        var subHistory = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userContent)
        };

        var settings = new ChatOptions
        {
            MaxOutputTokens = 4096
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
                var pluginServices = _serviceProvider.GetRequiredService<Services.ToolInvocation.ToolInvocationPipelineServices>();
                var chatOpts = Services.Maf.MafChatOptionsMapper.ToChatOptions(settings, allowedTools);
                chatOpts.Instructions = systemPrompt;
                var agentOpts = new Microsoft.Agents.AI.ChatClientAgentOptions { ChatOptions = chatOpts };
                var subtaskAgent = new Microsoft.Agents.AI.ChatClientAgent(chatClient, agentOpts, _loggerFactory, _runtime.GetPluginServices() ?? _serviceProvider)
                    .AsBuilder()
                    .Use(Services.ToolInvocation.ToolInvocationMiddleware.Create(_runtime.ToolRegistry, pluginServices))
                    .Build();
                var subtaskSession = await subtaskAgent.CreateSessionAsync(timeoutCts.Token).ConfigureAwait(false);
                var subtaskMessages = new List<ChatMessage> { new(ChatRole.User, userContent) };
                var runOpts = new Microsoft.Agents.AI.ChatClientAgentRunOptions(chatOpts);
                var toolCallArgBudget = new Dictionary<string, int>(StringComparer.Ordinal);
                var callState = new Dictionary<string, (string Name, string ArgsSoFar)>(StringComparer.Ordinal);
                await foreach (var update in subtaskAgent.RunStreamingAsync(subtaskMessages, subtaskSession, runOpts, timeoutCts.Token).ConfigureAwait(false))
                {
                    foreach (var d in Services.Maf.MafToolCallDeltaExtractor.ExtractFromAgentResponseUpdate(update, toolCallArgBudget, callState))
                        await SendSubtaskToolDeltaAsync(d).ConfigureAwait(false);
                    var text = update.Text;
                    if (text is { Length: > 0 })
                    {
                        fullResponse.Append(text);
                        await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_chunk", Content = text }).ConfigureAwait(false);
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

    /// <summary>供 compact_conversation 工具调用：主动压缩当前会话的最旧若干轮为摘要（MAF Compaction）。返回可展示给模型的结果文案。</summary>
    public async Task<string> CompactConversationAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "[错误] 无当前会话，无法压缩。";
        if (!_sessions.TryGetValue(sessionId, out var state))
            return "[错误] 会话不存在或已过期。";
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        var chatClient = _runtime.GetChatClient();
        if (chatClient == null)
            return "[错误] 未找到对话服务。";
        if (state.History.Count <= 3)
            return "[无需压缩] 当前对话轮次较少，无需压缩。";
        var beforeCount = state.History.Count;
#pragma warning disable MAAI001
        var strategy = BuildCompactionStrategy(chatClient, triggerTokens: 0, budgetTokens: 0);
        var compacted = await Microsoft.Agents.AI.Compaction.CompactionProvider.CompactAsync(
            strategy, state.History, _logger, ct).ConfigureAwait(false);
#pragma warning restore MAAI001
        var compactedList = compacted.ToList();
        if (compactedList.Count < beforeCount)
        {
            var removed = beforeCount - compactedList.Count;
            state.History.Clear();
            state.History.AddRange(compactedList);
            var sessionManager = _serviceProvider.GetService<SessionManager>();
            if (sessionManager != null)
            {
                await NotifyAgentTraceAsync(sessionManager, sessionId, "context",
                    $"手动压缩：{removed} 条消息已整理",
                    $"压缩前 {beforeCount} 条 → 压缩后 {compactedList.Count} 条", ct).ConfigureAwait(false);
            }
            var turns = Math.Max(1, removed / 2);
            return $"[已压缩] 已将约 {turns} 轮对话合并/整理，上下文已释放。";
        }
        return "[未压缩] 当前对话轮次或内容不足，未执行压缩。";
    }


    /// <summary>估算整个历史的 token 总数，含图片的视觉 token 估算。</summary>
    private static int EstimateHistoryTokens(IList<ChatMessage> history, ContextWindowConfig ctx) =>
        ContextManager.EstimateHistoryTokens(history, ctx);

    /// <summary>估算单条消息的 token 数，含图片的视觉 token 估算。</summary>
    private static int EstimateMessageTokens(ChatMessage msg, ContextWindowConfig ctx) =>
        ContextManager.EstimateMessageTokens(msg, ctx);

    /// <summary>获取当前生效的上下文 token 上限（优先使用当前模型的 ContextLength，否则用全局 ContextWindow.MaxContextTokens）。</summary>
    private int GetEffectiveMaxContextTokens()
    {
        var entry = GetActiveModelEntry();
        if (entry?.ContextLength is > 0)
            return entry.ContextLength.Value;
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        return ctx.MaxContextTokens > 0 ? ctx.MaxContextTokens : 64_000;
    }

    private void TrimHistory(List<ChatMessage> history)
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
    /// 仅当当前模型在设置中开启百炼 <c>enable_search</c> 时注入：抑制为「上网查资讯」而反复 <c>run_command</c> 开浏览器、<c>window.open</c> 搜索页、再用 <c>get_visible_text</c> 抠 SERP 的笨重路径。
    /// </summary>
    private const string EnableSearchSuppressionInstruction =
        "[联网检索] 当前对话模型已在设置中开启百炼「联网搜索」（enable_search）。"
        + "用户只要网络资讯、新闻、实时事实或「去网上查/搜一下」类需求时，优先直接作答，由服务端检索能力提供时效信息；"
        + "不要轻易使用 run_command 打开默认浏览器、run_custom_page_script（如 window.open 搜索页）或依赖 get_visible_text 抓取搜索结果页来替代检索。"
        + "例外：用户明确要求操作其正在浏览的**当前网页**（高亮、读可见内容、截图等）或内置检索明显不足以完成该任务时，再用 Browser 等工具。";

    /// <summary>
    /// 构建本轮流式请求用的消息列表：可选追加 client 身份后缀；再追加意图/事实约束与工具结果复述约束。
    /// </summary>
    private static List<ChatMessage> BuildHistoryForStreamingTurn(
        List<ChatMessage> stateHistory,
        string? identitySuffix,
        string? enableSearchSuppressionSuffix = null)
    {
        var historyToUse = stateHistory;
        if (!string.IsNullOrEmpty(identitySuffix) && stateHistory.Count > 0 && stateHistory[0].Role == ChatRole.System)
        {
            var sysMsg = stateHistory[0];
            var newSystemText = (sysMsg.Text ?? "") + "\n\n" + identitySuffix;
            historyToUse = new List<ChatMessage>(stateHistory.Count) { new(ChatRole.System, newSystemText) };
            for (var i = 1; i < stateHistory.Count; i++)
                historyToUse.Add(stateHistory[i]);
        }

        if (historyToUse.Count > 0 && historyToUse[0].Role == ChatRole.System)
        {
            var sys = historyToUse[0].Text ?? "";
            var augmented = sys;
            if (!string.IsNullOrEmpty(enableSearchSuppressionSuffix))
                augmented += "\n\n" + enableSearchSuppressionSuffix;
            augmented += "\n\n" + LatestIntentAndGroundedFactsInstruction + "\n\n" + ToolResultEchoSystemInstruction;
            var withEcho = new List<ChatMessage>(historyToUse.Count) { new(ChatRole.System, augmented) };
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

    /// <summary>按当前 turn 状态构建 MAF <see cref="MessageAIContextProvider"/> 数组，供主会话 Agent 注入上下文。</summary>
    private MessageAIContextProvider[] BuildContextProviders(Services.Chat.StreamChatTurnContext turn, SessionManager sm)
    {
        var providers = new List<MessageAIContextProvider>();

        var memorySvc = _serviceProvider.GetService<IMemoryStoreService>();
        if (memorySvc?.IsAvailable == true)
        {
            providers.Add(new MemoryContextProvider(
                memorySvc, turn.UserMessage, turn.SessionId, turn.CtxConfig, sm, _logger, turn.ContextWarnings));

            if (!string.IsNullOrWhiteSpace(turn.KnowledgeBaseId))
            {
                providers.Add(new KnowledgeBaseContextProvider(
                    memorySvc, turn.KnowledgeBaseId!.Trim(), turn.UserMessage, turn.SessionId, sm, _logger, turn.ContextWarnings));
            }
        }

        var taskStore = _serviceProvider.GetService<ICrossAgentTaskStore>();
        if (taskStore != null)
        {
            providers.Add(new CrossAgentTaskContextProvider(taskStore, sm, turn.SessionId, _logger));
        }

        if (!string.IsNullOrWhiteSpace(turn.PlanId))
        {
            providers.Add(new PlanContextProvider(
                () => turn.PlanResult, turn.PlanCurrentStepIndex, turn.CtxConfig.PlanContentMaxChars));
        }

        return providers.ToArray();
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

    /// <summary>向量检索用：截断并拼接最近一条历史，与两轮选择一致。</summary>
    private static string BuildToolSelectionUserPrompt(string userMessage, IReadOnlyList<ChatMessage>? recentHistory)
    {
        var userPrompt = (userMessage ?? "").Trim();
        if (userPrompt.Length > 1000)
            userPrompt = userPrompt[..1000] + "...";
        if (recentHistory != null && recentHistory.Count > 0)
        {
            var lastContent = recentHistory[^1].Text ?? "";
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
