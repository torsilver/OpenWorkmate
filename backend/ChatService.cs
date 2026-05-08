using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Plugins;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.Memory;
using OpenWorkmate.Server.Services.Plan;
using OpenWorkmate.Server.Services.CrossAgentTask;
using OpenWorkmate.Server.Services.ScheduledTask;
using OpenWorkmate.Server.Services.ContextProviders;
using OpenWorkmate.Server.Services.DashScope;
using OpenWorkmate.Server.Mcp;
using OpenWorkmate.Server.Services.Chat;
using OpenWorkmate.Server.Services.DynamicTooling;
using OpenWorkmate.Server.Services.Maf;
using OpenWorkmate.Server.Services.Subagent;
using OpenWorkmate.Server.Services.ModelProfiles;
using OpenWorkmate.Server.Services.OpenAiCompat;
using OpenWorkmate.Server.Services.ModelAdapters;
using OpenWorkmate.Server.Services.Telemetry;
using OpenWorkmate.Server.Logging;

namespace OpenWorkmate.Server;

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
    private readonly IServiceProvider _serviceProvider;
    private readonly IChatRuntimeAccessor _runtime;
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly IPlanStore _planStore;
    private readonly AgentDebugStatsService _agentDebugStats;
    private readonly IChatSessionStore _chatSessionStore;
    private readonly IBuiltinTurnCompletionVerifier _builtinTurnCompletionVerifier;
    private readonly SubtaskTimelineBlockCoordinator _subtaskTimelineBlocks;
    private readonly TimelineBlockStreamCoordinator _timelineBlockCoordinator;
    private readonly ITelemetryRelayQueue? _telemetryRelay;
    private readonly ITelemetryTransmissionPolicyProvider _telemetryTransmissionPolicy;
    private readonly ModelProfileRegistry _modelProfiles;
    private readonly IChatClientPipelineFactory _chatClientPipelineFactory;
    private readonly object _runtimeLock = new();

    public ChatService(IConfiguration config, ILogger<ChatService> logger, ILoggerFactory loggerFactory, ConfigService configService, SkillService skillService, McpClientManager mcpManager, IServiceProvider serviceProvider, IChatRuntimeAccessor runtimeAccessor, EmbeddingProvider embeddingProvider, IPlanStore planStore, AgentDebugStatsService agentDebugStats, IChatSessionStore chatSessionStore, IBuiltinTurnCompletionVerifier builtinTurnCompletionVerifier, SubtaskTimelineBlockCoordinator subtaskTimelineBlocks, TimelineBlockStreamCoordinator timelineBlockCoordinator, ITelemetryTransmissionPolicyProvider telemetryTransmissionPolicy, ModelProfileRegistry modelProfiles, IChatClientPipelineFactory chatClientPipelineFactory, ITelemetryRelayQueue? telemetryRelay = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configService = configService;
        _skillService = skillService;
        _mcpManager = mcpManager;
        _serviceProvider = serviceProvider;
        _runtime = runtimeAccessor;
        _embeddingProvider = embeddingProvider;
        _planStore = planStore;
        _agentDebugStats = agentDebugStats;
        _chatSessionStore = chatSessionStore;
        _builtinTurnCompletionVerifier = builtinTurnCompletionVerifier;
        _subtaskTimelineBlocks = subtaskTimelineBlocks;
        _timelineBlockCoordinator = timelineBlockCoordinator;
        _telemetryTransmissionPolicy = telemetryTransmissionPolicy;
        _telemetryRelay = telemetryRelay;
        _modelProfiles = modelProfiles;
        _chatClientPipelineFactory = chatClientPipelineFactory;

        var session = configService.Current.Session ?? new SessionConfig();
        var cleanupInterval = session.CleanupIntervalMinutes;

        _configService.OnConfigChanged += () => _ = RebuildRuntimeAsync();
        _skillService.OnSkillsChanged += () => _ = RebuildRuntimeAsync();

        _cleanupTimer = new System.Threading.Timer(CleanupExpiredSessions, null,
            TimeSpan.FromMinutes(cleanupInterval),
            TimeSpan.FromMinutes(cleanupInterval));
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

    /// <summary>重建 Kernel（内置插件 + 用户 Skill + MCP）。</summary>
    public async Task RebuildRuntimeAsync()
    {
        _modelProfiles.Reload();
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

                chatClients[entry.Id] = _chatClientPipelineFactory.CreateChatClient(entry, modelId, endpointUri, apiKey);
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

        var pluginInstances = new List<object>();

        void TryAddBuiltInPlugin(object instance)
        {
            var attr = instance.GetType().GetCustomAttribute<OpenWorkmatePluginIdAttribute>()
                ?? throw new InvalidOperationException(
                    $"内置插件类型 {instance.GetType().FullName} 缺少 [OpenWorkmatePluginId]。");
            if (disabledBuiltIn.Contains(attr.Id.ToLowerInvariant()))
                return;
            pluginInstances.Add(instance);
        }

        TryAddBuiltInPlugin(new CliPlugin());
        TryAddBuiltInPlugin(new ExcelPlugin(_loggerFactory.CreateLogger<ExcelPlugin>()));
        TryAddBuiltInPlugin(new WordPlugin(_loggerFactory.CreateLogger<WordPlugin>()));
        TryAddBuiltInPlugin(new PptPlugin(_loggerFactory.CreateLogger<PptPlugin>()));
        TryAddBuiltInPlugin(new OfficeLegacyConvertPlugin(_loggerFactory.CreateLogger<OfficeLegacyConvertPlugin>()));

        var rpcManager = _serviceProvider.GetRequiredService<RpcManager>();
        var screenshotCache = _serviceProvider.GetRequiredService<ScreenshotCacheService>();
        var attachmentCache = _serviceProvider.GetRequiredService<AttachmentCacheService>();
        var browserPluginLogger = _loggerFactory.CreateLogger<BrowserPlugin>();
        var filePluginLogger = _loggerFactory.CreateLogger<FilePlugin>();
        TryAddBuiltInPlugin(new BrowserPlugin(sessionManager, rpcManager, screenshotCache, browserPluginLogger));
        TryAddBuiltInPlugin(new FilePlugin(screenshotCache, attachmentCache, filePluginLogger));
        TryAddBuiltInPlugin(new SystemPlugin());
        var transcribeService = _serviceProvider.GetRequiredService<ITranscribeService>();
        var sttPluginLogger = _loggerFactory.CreateLogger<SttPlugin>();
        TryAddBuiltInPlugin(new SttPlugin(transcribeService, sttPluginLogger));
        var ocrService = _serviceProvider.GetRequiredService<IOcrService>();
        var ocrPluginLogger = _loggerFactory.CreateLogger<OcrPlugin>();
        TryAddBuiltInPlugin(new OcrPlugin(ocrService, ocrPluginLogger));
        TryAddBuiltInPlugin(new PdfPlugin(_loggerFactory.CreateLogger<PdfPlugin>()));

        var currentDocLogger = _loggerFactory.CreateLogger<CurrentDocumentPlugin>();
        TryAddBuiltInPlugin(new CurrentDocumentPlugin(sessionManager, rpcManager, currentDocLogger));

        var clawhubRunner = _serviceProvider.GetRequiredService<ClawhubScriptRunner>();
        TryAddBuiltInPlugin(new ClawhubSkillPlugin(_skillService, clawhubRunner, _configService, _loggerFactory.CreateLogger<ClawhubSkillPlugin>()));

        if (_embeddingProvider.IsConfigured)
        {
            var memorySvc = _serviceProvider.GetRequiredService<IMemoryStoreService>();
            TryAddBuiltInPlugin(new MemoryPlugin(memorySvc, sessionManager, _loggerFactory.CreateLogger<MemoryPlugin>()));
        }

        TryAddBuiltInPlugin(new CompactConversationPlugin(this));
        TryAddBuiltInPlugin(new SubagentPlugin(this));

        var taskStore = _serviceProvider.GetRequiredService<ICrossAgentTaskStore>();
        TryAddBuiltInPlugin(new CrossAgentTaskPlugin(taskStore, sessionManager, _loggerFactory.CreateLogger<CrossAgentTaskPlugin>()));

        TryAddBuiltInPlugin(new AgentToolingPlugin(_runtime, sessionManager, _loggerFactory.CreateLogger<AgentToolingPlugin>()));
        TryAddBuiltInPlugin(new UserSkillProgressivePlugin(_skillService, _loggerFactory.CreateLogger<UserSkillProgressivePlugin>()));

        var planPlugin = _serviceProvider.GetRequiredService<PlanPlugin>();
        TryAddBuiltInPlugin(planPlugin);

        var skillAuthorPlugin = _serviceProvider.GetRequiredService<SkillAuthorPlugin>();
        TryAddBuiltInPlugin(skillAuthorPlugin);

        var userOptionsManager = _serviceProvider.GetRequiredService<UserOptionsManager>();
        var userOptionsLogger = _loggerFactory.CreateLogger<UserOptionsPlugin>();
        TryAddBuiltInPlugin(new UserOptionsPlugin(userOptionsManager, userOptionsLogger));

        TryAddBuiltInPlugin(new AccurateDataPlugin(_configService));

        var meetingStore = _serviceProvider.GetRequiredService<IMeetingTranscriptStore>();
        TryAddBuiltInPlugin(new MeetingTranscriptPlugin(meetingStore));

        var scheduledTaskStore = _serviceProvider.GetRequiredService<IScheduledTaskStore>();
        TryAddBuiltInPlugin(new ScheduledTaskPlugin(scheduledTaskStore));

        var toolRegistry = new ToolRegistry();
        foreach (var instance in pluginInstances)
            toolRegistry.RegisterPluginFromObject(instance);

        var skillCount = _skillService.GetAllSkills().Count(s => s.Enabled);

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

        foreach (var entry in entries)
        {
            var pKey = (entry.ModelProfileKey ?? "").Trim();
            if (pKey.Length == 0) continue;
            if (!_modelProfiles.TryGetMerged(pKey, out var prof) || prof is null)
            {
                _logger.LogWarning("AiModelEntry {EntryId} modelProfileKey={ProfileKey} not found in ModelProfileRegistry.", entry.Id, pKey);
                continue;
            }

            _logger.LogInformation(
                "ModelProfile bound: entry={EntryId} profileKey={ProfileKey} maxInputTokens={MaxIn} supportsVisionProfile={ProfVision} supportsVisionEntry={EntryVision} suppressThinkingTools={Suppress}",
                entry.Id, prof.ProfileKey, prof.MaxInputTokens, prof.SupportsVision, entry.SupportsVision, prof.SuppressUpstreamThinkingWithTools);
            if (entry.SupportsVision != prof.SupportsVision)
            {
                _logger.LogWarning(
                    "ModelProfile vs AiModelEntry SupportsVision mismatch: entry={EntryId} profileKey={ProfileKey} profile={ProfVision} entry={EntryVision}",
                    entry.Id, prof.ProfileKey, prof.SupportsVision, entry.SupportsVision);
            }

            if (entry.ContextLength is null && prof.MaxInputTokens is int maxIn)
            {
                _logger.LogInformation(
                    "ModelProfile hint: entry={EntryId} ContextLength unset; excerpt suggests max_input_tokens≈{MaxIn} (not auto-applied).",
                    entry.Id, maxIn);
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

        if (activeEntry != null
            && _modelProfiles.TryGetMergedForModelEntry(activeEntry, out var activeProf)
            && activeProf is not null)
        {
            _logger.LogInformation(
                "Active model ModelProfile: profileKey={ProfileKey} requiresReasoningEcho={Echo} suppressUpstreamThinkingTools={Suppress} disableReasoningHttpEcho={DisableEcho} useThinkingKeepAll={KeepAll}",
                activeProf.ProfileKey, activeProf.RequiresReasoningEchoWithTools, activeProf.SuppressUpstreamThinkingWithTools,
                activeProf.DisableReasoningHttpEcho, activeProf.UseThinkingKeepAll);
        }
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

    private const int ProgressiveSkillMetadataMaxCount = 32;
    private const int ProgressiveSkillDescriptionMaxChars = 200;

    /// <summary>渐进式技能 Level1：仅 Id + 描述，正文需 <c>load_user_skill_instructions</c> 按需加载（与动态工具检索分离）。</summary>
    private string BuildProgressiveUserSkillMetadataBlock()
    {
        var skills = _skillService.GetAllSkills()
            .Where(s => s.Enabled)
            .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Take(ProgressiveSkillMetadataMaxCount)
            .ToList();
        if (skills.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("[渐进式用户技能 · 元数据]");
        sb.AppendLine("下列条目仅含发现信息；完整说明未载入上下文。只要本轮可能需要业务写盘或严格版式/流程，默认请先完成技能链：search_available_skills → select_skill_for_turn →（按需）load_user_skill_instructions（skillId 用下列 Id 或检索结果中的 Id），再 search_available_tools / activate_tools。纯情感闲聊等无须技能与业务工具时，不必强行检索。");
        sb.AppendLine("search_available_skills 的返回不是技能正文：在按某技能规则写 Word/改格式前，必须再调用 load_user_skill_instructions，否则模型并未看到 SKILL 细则。");
        sb.AppendLine("技能正文与 references/ 等附属文件仅通过 load_user_skill_instructions 按需读取；勿依赖 search_available_tools 发现技能。");
        foreach (var s in skills)
        {
            var desc = (s.Description ?? "").Trim();
            if (desc.Length > ProgressiveSkillDescriptionMaxChars)
                desc = desc[..ProgressiveSkillDescriptionMaxChars] + "…";
            sb.Append("- Id: ").Append(s.Id);
            if (desc.Length > 0)
                sb.Append(" — ").Append(desc);
            sb.AppendLine();
        }

        if (_skillService.GetAllSkills().Count(x => x.Enabled) > ProgressiveSkillMetadataMaxCount)
            sb.Append("(其余技能见 Chrome 设置 → 技能列表。)");
        return sb.ToString().TrimEnd();
    }

    private string GetActiveSystemPrompt()
    {
        var entry = GetActiveModelEntry();
        var prompt = entry?.SystemPrompt?.Trim();
        var basePrompt = !string.IsNullOrEmpty(prompt)
            ? prompt
            : AiEmbeddedDefaults.DefaultSystemPrompt.Trim();
        var guidance = BuiltinTaskPluginSystemGuidance.Trim();
        var progressive = BuildProgressiveUserSkillMetadataBlock();
        string core;
        if (string.IsNullOrEmpty(basePrompt))
            core = guidance;
        else
            core = basePrompt + "\n\n" + guidance;
        if (string.IsNullOrEmpty(progressive))
            return core;
        return core + "\n\n" + progressive;
    }

    /// <summary>首条 system：全局模型 prompt + 内置插件说明 + 当前会话 Agent 的 <c>systemPromptSuffix</c>（须在持有 <see cref="_runtimeLock"/> 时调用 <see cref="GetActiveSystemPrompt"/>）。</summary>
    private string BuildInitialSystemPromptForSessionUnderLock(string sessionId)
    {
        var basePrompt = GetActiveSystemPrompt();
        var sm = _serviceProvider.GetRequiredService<SessionManager>();
        var pid = sm.GetAgentProfileId(sessionId);
        var suffix = _configService.GetAgentSystemPromptSuffix(pid);
        if (string.IsNullOrEmpty(suffix)) return basePrompt;
        return basePrompt + "\n\n" + suffix.Trim();
    }

    public List<ChatMessage> GetSessionHistory(string sessionId)
    {
        string systemPrompt;
        lock (_runtimeLock)
            systemPrompt = BuildInitialSystemPromptForSessionUnderLock(sessionId);
        var state = _sessions.GetOrAdd(sessionId, _ => LoadOrCreateSessionState(sessionId, systemPrompt));
        return state.History;
    }

    /// <summary>从磁盘恢复或新建会话状态（内存淘汰后仍可继续对话）。</summary>
    private SessionState LoadOrCreateSessionState(string sessionId, string systemPrompt)
    {
        var persisted = _chatSessionStore.TryGetPersistedMessages(sessionId);
        if (persisted == null || persisted.Count == 0)
            return new SessionState(systemPrompt);

        var state = new SessionState(systemPrompt);
        foreach (var m in persisted)
        {
            if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                state.History.Add(new ChatMessage(ChatRole.User, m.Text));
            else if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                state.History.Add(new ChatMessage(ChatRole.Assistant, m.Text));
        }

        state.Touch();
        return state;
    }

    /// <summary>删除内存中的会话（历史对话「删除」时与落盘一并清理）。</summary>
    public bool TryRemoveSession(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>将当前内存中的 History 覆盖写入 transcript（每轮流式结束后调用）。</summary>
    public async Task PersistSessionTranscriptAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return;
        try
        {
            var sm = _serviceProvider.GetRequiredService<SessionManager>();
            var agentPid = sm.GetAgentProfileId(sessionId);
            await _chatSessionStore.SaveFromHistoryAsync(sessionId, state.History, agentPid, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persist chat transcript failed for session {SessionId}", sessionId);
        }
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
            systemPrompt = BuildInitialSystemPromptForSessionUnderLock(sessionId);
        }

        var state = _sessions.GetOrAdd(sessionId, _ => LoadOrCreateSessionState(sessionId, systemPrompt));
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
            var roundId = Guid.NewGuid().ToString("N");
            SessionContext.SetRoundId(roundId);
            var turn = new StreamChatTurnContext
            {
                RoundId = roundId,
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

            _logger.LogInformation("[{SessionId}] [{RoundId}] Chat turn started (workflow).", sessionId, roundId);

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
                await foreach (var streamItem in MafAgentGroupChatSessionRunner.InvokeStreamingAsync(
                                   _runtime, _loggerFactory, turn.HistoryToUse, turn.ExecSettings, turn.SessionManager, sessionId, ct)
                               .ConfigureAwait(false))
                {
                    if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                        fullResponse.Append(streamItem.Content);
                    yield return streamItem;
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

                var streamOutcome = new StreamPassOutcome();
                ContextTurnSnapshot.TryLogAndOptionalFile(sessionId, turn.RoundId, turn.HistoryToUse, _logger);
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
                    var mafHistoryBaselineCount = turn.HistoryToUse.Count;
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
                                           dynamicTooling: turn.DynamicToolingState,
                                           mergePlanIntoDynamicBootstrap: turn.PlanResult != null,
                                           ct: ct).ConfigureAwait(false))
                        {
                            if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                                fullResponse.Append(streamItem.Content);
                            yield return streamItem;
                        }

                        if (streamOutcome.ContextLengthRetryRequested)
                        {
                            turn.HistoryToUse = SystemPromptBuilder.BuildHistoryForStreamingTurn(state.History, turn.IdentitySuffix, turn.EnableSearchSuppressionSuffix);
                            continue;
                        }

                        break;
                    }

                    _logger.LogInformation(
                        "[{SessionId}] [{RoundId}] MAF 主会话流结束（百炼 reasoning 经 MafMainSessionStreamRunner / DashScope* 桥；若 SSE tap 与 Drain 不一致请查 AsyncLocal）",
                        sessionId, turn.RoundId);

                    var visibleAssistantForVerifier = ReasoningTagStreamParser.StripReasoningTags(fullResponse.ToString()).Trim();
                    if (ShouldInvokeBuiltinCompletionVerifier(turn, visibleAssistantForVerifier))
                    {
                        var dts = turn.DynamicToolingState!;
                        var bizNames = GetActivatedBusinessToolNamesForVerifier(dts);
                        var verifierReq = new TurnCompletionVerifierRequest(
                            turn.UserMessage,
                            visibleAssistantForVerifier,
                            dts.SearchInvocationCount,
                            dts.ActivateInvocationCount,
                            bizNames);
                        var eval = await _builtinTurnCompletionVerifier.EvaluateAsync(verifierReq, ct).ConfigureAwait(false);
                        _logger.LogInformation(
                            "[{SessionId}] Builtin completion verifier: outcome={Outcome} parseOk={ParseOk} turnRoute={Route}",
                            sessionId, eval.Outcome, eval.ParseOk, turn.TurnRoute);

                        if (eval.ParseOk && eval.Outcome == TurnCompletionVerifierOutcome.NeedMoreWork)
                        {
                            _logger.LogInformation(
                                "[{SessionId}] Builtin completion verifier: triggering one MAF continuation pass (historyBaseline={Baseline}, historyNow={Now}).",
                                sessionId, mafHistoryBaselineCount, turn.HistoryToUse.Count);
                            yield return new StreamItem(
                                IsWarning: true,
                                Content: BuiltinTurnCompletionMessages.StreamWarningBeforeContinuation);
                            AppendContinuationHistoryAfterVerifier(turn, visibleAssistantForVerifier, mafHistoryBaselineCount);
                            var continuationOutcome = new StreamPassOutcome();
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
                                               continuationOutcome,
                                               contextAttemptIndex: 0,
                                               requireToolInvocation: false,
                                               contextProviders: contextProviders,
                                               dynamicTooling: turn.DynamicToolingState,
                                               mergePlanIntoDynamicBootstrap: turn.PlanResult != null,
                                               ct: ct).ConfigureAwait(false))
                            {
                                if (!streamItem.IsWarning && streamItem.Kind == StreamSegmentKind.Normal && !string.IsNullOrEmpty(streamItem.Content))
                                    fullResponse.Append(streamItem.Content);
                                yield return streamItem;
                            }

                            if (continuationOutcome.ContextLengthRetryRequested)
                            {
                                _logger.LogWarning(
                                    "[{SessionId}] Builtin completion continuation pass requested context-length retry; not chaining another rebuild in this experimental path.",
                                    sessionId);
                            }
                        }
                        else if (eval.ParseOk && eval.Outcome == TurnCompletionVerifierOutcome.AskUser)
                        {
                            var clarify = await GenerateAskUserClarificationAsync(
                                turn.UserMessage, visibleAssistantForVerifier, eval.Reason, ct).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(clarify))
                            {
                                fullResponse.Clear();
                                fullResponse.Append(clarify);
                                yield return new StreamItem(IsWarning: false, Content: clarify);
                            }
                            else if (!string.IsNullOrWhiteSpace(eval.Reason))
                            {
                                fullResponse.Clear();
                                fullResponse.Append(eval.Reason);
                                yield return new StreamItem(IsWarning: false, Content: eval.Reason);
                            }
                        }
                    }
                }
            }

            var assistantText = ReasoningTagStreamParser.StripReasoningTags(fullResponse.ToString());
            state.History.Add(new ChatMessage(ChatRole.Assistant, assistantText));
            if (assistantText.Length > 0)
                TryEnqueueAssistantTurnTelemetry(sessionManagerForStatus, sessionId, assistantText);
            var preview = assistantText.Length > 0 ? LogPreview.HeadTail(assistantText, 64, 64) : "";
            _logger.LogInformation("[{SessionId}] [{RoundId}] Turn completed, turns={Turns}, assistantChars={AssistantChars}, assistantPreview={Preview}",
                sessionId, turn.RoundId, state.History.Count, assistantText.Length, preview);
        }
        finally
        {
            SessionContext.SetRoundId(null);
        }
    }

    /// <summary>主会话一轮结束时的可见助手正文（已剥离 reasoning 标签）；不含 thinking/reasoning 流。</summary>
    private void TryEnqueueAssistantTurnTelemetry(SessionManager sessions, string sessionId, string assistantText)
    {
        if (_telemetryRelay is null || assistantText.Length == 0) return;
        const int maxChars = 50_000;
        var fullLen = assistantText.Length;
        var truncated = fullLen > maxChars;
        var message = truncated ? assistantText[..maxChars] : assistantText;
        var modelEntry = GetActiveModelEntry();
        var modelId = modelEntry?.Id;
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["charCount"] = fullLen,
            ["truncated"] = truncated,
            ["activeModelId"] = modelId
        });
        _telemetryRelay.TryEnqueueFromSession(
            _configService,
            _telemetryTransmissionPolicy,
            sessions,
            sessionId,
            "assistant_turn_final",
            "p1",
            message,
            modelId,
            payload);
    }

    /// <summary>供 run_subtask 工具调用：在隔离的上下文中执行子任务，仅将最终自然语言结果返回给主 Agent，不把子任务内的多轮 tool 调用塞入主会话历史。</summary>
    public Task<string> RunSubtaskAsync(string sessionId, string taskDescription, string? constraints, CancellationToken ct = default) =>
        RunSubtaskWithPresetAsync(sessionId, SubagentBuiltinPreset.General, taskDescription, constraints, ct);

    /// <summary>供内置子代理预设与 <c>run_subtask</c> 共用：隔离上下文执行，仅将总结返回主 Agent。</summary>
    public async Task<string> RunSubtaskWithPresetAsync(
        string sessionId,
        SubagentBuiltinPreset preset,
        string taskDescription,
        string? constraints,
        CancellationToken ct = default)
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
        var wpsHostKind = string.Equals(clientType, "wps", StringComparison.OrdinalIgnoreCase)
            ? sessionManager.GetWpsHostKind(sessionId)
            : null;
        var allTools = _runtime.GetAllowedTools(clientType, sessionId, wpsHostKind);
        var registry = _runtime.ToolRegistry;
        var allowedTools = SubagentBuiltinPresets.FilterToolsForSubtask(registry, allTools, preset, out var filterError);
        if (filterError != null)
            return filterError;
        if (allowedTools.Count == 0)
            return "[错误] 当前端无可用的工具集，无法执行子任务。";

        var systemPrompt = SubagentBuiltinPresets.BuildSystemInstructions(preset);
        var userContent = taskDescTrimmed;
        if (!string.IsNullOrWhiteSpace(constraints))
            userContent += "\n\n约束：" + constraints.Trim();

        var settings = new ChatOptions
        {
            MaxOutputTokens = 4096
        };
        var fullResponse = new System.Text.StringBuilder();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        SubtaskContext.SetActive(true);
        var subtaskTimelineBegun = false;
        Task SendSubtaskToolDeltaAsync(ToolCallStreamDelta d) =>
            SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage
            {
                Type = "tool_call_delta",
                ToolCallId = d.CallId,
                ToolName = d.ToolName,
                ArgumentsDelta = string.IsNullOrEmpty(d.ArgumentsDelta) ? null : d.ArgumentsDelta,
                IsSubtask = true
            });
        async Task SendSubtaskReasoningChunkAsync(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            var (bs, bk) = _subtaskTimelineBlocks.EnsureChunkBlock(sessionId, SubtaskTimelineBlockCoordinator.KindThink);
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage
            {
                Type = "reasoning_chunk",
                Content = delta,
                IsSubtask = true,
                BlockSeq = bs,
                BlockKind = bk
            }).ConfigureAwait(false);
        }

        try
        {
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage
            {
                Type = "subtask_start",
                TaskDescription = taskDescTrimmed,
                Constraints = string.IsNullOrWhiteSpace(constraints) ? null : constraints.Trim(),
                SubtaskPreset = preset switch
                {
                    SubagentBuiltinPreset.Explore => "explore",
                    SubagentBuiltinPreset.CliShell => "cliShell",
                    SubagentBuiltinPreset.Browser => "browser",
                    _ => null
                }
            }).ConfigureAwait(false);
            _subtaskTimelineBlocks.BeginSubtaskRun(sessionId);
            subtaskTimelineBegun = true;

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
                    foreach (var reasoningDelta in DashScopeReasoningSessionBridge.DrainForSession(sessionId))
                        await SendSubtaskReasoningChunkAsync(reasoningDelta).ConfigureAwait(false);
                    foreach (var reasoningDelta in DashScopeReasoningContext.DrainCurrentFrame())
                        await SendSubtaskReasoningChunkAsync(reasoningDelta).ConfigureAwait(false);
                    var text = update.Text;
                    if (text is { Length: > 0 })
                    {
                        fullResponse.Append(text);
                        await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_chunk", Content = text }).ConfigureAwait(false);
                    }
                }
            }

            var result = fullResponse.ToString().Trim();
            var endContent = string.IsNullOrEmpty(result) ? null : result;
            await SendSubtaskMessageAsync(sessionManager, sessionId, new WsMessage { Type = "subtask_end", Content = endContent ?? "" }).ConfigureAwait(false);
            return string.IsNullOrEmpty(result) ? "[子任务未返回文本结果]" : result;
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
            if (subtaskTimelineBegun)
                _subtaskTimelineBlocks.EndSubtaskRun(sessionId);
            SubtaskContext.SetActive(false);
        }
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

    private static bool ShouldInvokeBuiltinCompletionVerifier(StreamChatTurnContext turn, string visibleAssistantTrimmed)
    {
        var dts = turn.DynamicToolingState;
        if (dts == null)
            return false;

        if (turn.TurnRoute == TurnRoute.UnclearOrChitchat)
            return true;

        if (string.IsNullOrWhiteSpace(visibleAssistantTrimmed)
            && dts.SearchInvocationCount + dts.ActivateInvocationCount > 0)
            return true;

        if (!dts.HasActivatedAnyBusinessTool()
            && TurnRouteClassifier.LooksLikeTaskUserMessage(turn.UserMessage)
            && !string.IsNullOrWhiteSpace(visibleAssistantTrimmed))
            return true;

        return false;
    }

    private async Task<string> GenerateAskUserClarificationAsync(
        string userMessage,
        string assistantVisible,
        string? verifierReason,
        CancellationToken ct)
    {
        var client = _runtime.GetChatClient();
        if (client == null)
            return "";

        const string system =
            "你是办公助手。根据用户输入与助手已给出的可见回复，用一两句礼貌、简短的中文向用户追问以澄清意图或补充信息；不要编造事实；不要提及「系统」「评判」等词。";
        var u = userMessage.Trim();
        var av = assistantVisible.Length > 1200 ? assistantVisible[..1200] + "\n[…]" : assistantVisible;
        var userBlock = $"用户说：{u}\n\n助手已回复：{av}\n\n备注：{verifierReason?.Trim() ?? ""}";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, userBlock)
        };
        var options = new ChatOptions { MaxOutputTokens = 256, Temperature = 0.3f };
        try
        {
            using (DashScopeCallKindContext.EnterBackground())
            {
                var response = await client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
                var t = ReasoningTagStreamParser.StripReasoningTags(response.Text ?? "").Trim();
                return t.Length > 0 ? t : "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GenerateAskUserClarificationAsync failed.");
            return "";
        }
    }

    private static List<string> GetActivatedBusinessToolNamesForVerifier(DynamicToolingTurnState dts)
    {
        var list = new List<string>();
        foreach (var n in dts.ActivatedFunctionNames)
        {
            if (!DynamicToolingConstants.MetaFunctionNames.Contains(n))
                list.Add(n);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// 若 MAF 未把本轮助手消息写入 <paramref name="turn"/>.HistoryToUse，则先追加可见正文（可为空）再追加续跑 user 提示；否则仅追加续跑提示。
    /// </summary>
    private static void AppendContinuationHistoryAfterVerifier(
        StreamChatTurnContext turn,
        string visibleAssistant,
        int historyCountBeforeFirstMafPass)
    {
        if (turn.HistoryToUse.Count == historyCountBeforeFirstMafPass)
            turn.HistoryToUse.Add(new ChatMessage(ChatRole.Assistant, visibleAssistant));
        turn.HistoryToUse.Add(new ChatMessage(ChatRole.User, BuiltinTurnCompletionMessages.ContinuationUserNudge));
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
