using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Logging;
using OpenWorkmate.Server.Services.Chat;
using OpenWorkmate.Server.Services.DashScope;
using OpenWorkmate.Server.Services.DynamicTooling;
using OpenWorkmate.Server.Services.OpenAiCompat;
using OpenWorkmate.Server.Services.LlmRouting;
using OpenWorkmate.Server.Services.ModelProfiles;
using OpenWorkmate.Server.Services.Telemetry;

namespace OpenWorkmate.Server.Services.ModelAdapters;

/// <summary>默认 OpenAI 兼容对话管道：可选百炼 SSE reasoning 旁路，其余与历史 <see cref="ChatService"/> 内联实现一致。</summary>
public sealed class DefaultChatClientPipelineFactory : IChatClientPipelineFactory
{
    private readonly ConfigService _configService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ModelProfileRegistry _modelProfiles;
    private readonly TimelineBlockStreamCoordinator _timelineBlockCoordinator;
    private readonly ITelemetryTransmissionPolicyProvider _telemetryTransmissionPolicy;

    public DefaultChatClientPipelineFactory(
        ConfigService configService,
        ILoggerFactory loggerFactory,
        ModelProfileRegistry modelProfiles,
        TimelineBlockStreamCoordinator timelineBlockCoordinator,
        ITelemetryTransmissionPolicyProvider telemetryTransmissionPolicy)
    {
        _configService = configService;
        _loggerFactory = loggerFactory;
        _modelProfiles = modelProfiles;
        _timelineBlockCoordinator = timelineBlockCoordinator;
        _telemetryTransmissionPolicy = telemetryTransmissionPolicy;
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(AiModelEntry entry, string resolvedModelId, Uri? endpointUri, string apiKey)
    {
        var entryId = (entry.Id ?? "").Trim();
        var cfg = _configService.Current;
        var gatewayMode = cfg.TelemetryEnabled
            && cfg.TelemetryUserObservabilityEnabled != false
            && _telemetryTransmissionPolicy.IsTelemetryPolicyHealthy
            && string.Equals(_telemetryTransmissionPolicy.EffectiveRouteMode, "gateway", StringComparison.OrdinalIgnoreCase);
        var gwBase = TelemetryRelayDefaults.GetEffectiveRelayBaseUrl(cfg);
        var endpointTrimmed = (entry.Endpoint ?? "").Trim();
        HttpMessageHandler innerChain = new HttpClientHandler();
        if (gatewayMode && !string.IsNullOrEmpty(gwBase))
        {
            var upstream = endpointTrimmed.Length > 0
                ? endpointTrimmed.TrimEnd('/')
                : "https://dashscope.aliyuncs.com/compatible-mode/v1";
            innerChain = new LlmGatewayHeadersHandler(upstream, innerChain);
            endpointUri = new Uri(gwBase.TrimEnd('/') + "/llm/v1");
        }

        var logHandler = new OpenAiLoggingHandler(_loggerFactory.CreateLogger<OpenAiLoggingHandler>(), innerChain);

        HttpMessageHandler beforeProfile = logHandler;
        if (DashScopeChatRequestMerge.ShouldAttachDashScopeOpenAiCompatHandler(endpointUri))
        {
            beforeProfile = new DashScopeOpenAiCompatHandler(
                _configService,
                entryId,
                logHandler,
                _loggerFactory.CreateLogger<DashScopeOpenAiCompatHandler>(),
                _timelineBlockCoordinator);
        }
        else
        {
            beforeProfile = new OpenAiReasoningSseTapDelegatingHandler(
                entryId,
                logHandler,
                _loggerFactory.CreateLogger<OpenAiReasoningSseTapDelegatingHandler>(),
                _timelineBlockCoordinator);
        }

        var profileMergeHandler = new ModelProfileChatRequestMergeHandler(
            _configService,
            entryId,
            _modelProfiles,
            beforeProfile,
            _loggerFactory.CreateLogger<ModelProfileChatRequestMergeHandler>());
        var reasoningEchoHandler = new OpenAiReasoningEchoHandler(
            _configService,
            entryId,
            _modelProfiles,
            profileMergeHandler,
            _timelineBlockCoordinator,
            _loggerFactory.CreateLogger<OpenAiReasoningEchoHandler>());
        var httpClient = new HttpClient(reasoningEchoHandler);
        var options = new OpenAI.OpenAIClientOptions();
        if (endpointUri != null) options.Endpoint = endpointUri;
        options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient);
        var credential = new System.ClientModel.ApiKeyCredential(
            string.IsNullOrEmpty(apiKey) ? "placeholder" : apiKey);
        var openAiClient = new OpenAI.OpenAIClient(credential, options);
        var inner = openAiClient.GetChatClient(resolvedModelId).AsIChatClient();
        inner = new DynamicToolingChatOptionsSyncChatClient(
            inner,
            _loggerFactory.CreateLogger<DynamicToolingChatOptionsSyncChatClient>());
        return new FunctionInvokingChatClient(inner, _loggerFactory, null)
        {
            IncludeDetailedErrors = true
        };
    }
}
