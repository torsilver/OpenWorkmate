using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Telemetry;

public interface ITelemetryTransmissionPolicyProvider
{
    /// <summary>当前缓存的传输策略（来源：AI Gateway JSON）。</summary>
    TelemetryTransmissionPolicyFile GetCurrentPolicy();

    /// <summary>最近一次成功从 AI Gateway 拉取并校验通过；<c>false</c> 时结构化遥测 fail-closed（不入队/不 POST ingest）。</summary>
    bool IsTelemetryPolicyHealthy { get; }

    /// <summary>Gateway <c>availableEventKinds</c> 集合；仅在 <see cref="IsTelemetryPolicyHealthy"/> 为 <c>true</c> 时非空。</summary>
    IReadOnlySet<string> RelayAllowedEventKinds { get; }

    /// <summary>远端聚合策略中的 <c>ingestLogLevel</c>（error/information/debug 等）；与 WebSocket 会话值取更严一侧；不健康或未拉取时为 <c>null</c>。</summary>
    string? RelayIngestLogLevelCap { get; }

    /// <summary>有效路由 <c>gateway</c> | <c>direct</c>；不健康时视为 <c>direct</c>。</summary>
    string EffectiveRouteMode { get; }
}

/// <summary>
/// 从 AI Gateway <c>GET /api/policy/aggregated</c> 拉取策略并定时刷新（默认 30s + 配置变更）。
/// 拉取失败、解析失败、<c>telemetryEmissionAllowed=false</c> 或 <c>availableEventKinds</c> 为空时 <see cref="IsTelemetryPolicyHealthy"/> 为 <c>false</c>（fail-closed）。
/// </summary>
public sealed class TelemetryTransmissionPolicyBackgroundService : BackgroundService, ITelemetryTransmissionPolicyProvider
{
    private readonly object _policyLock = new();

    private TelemetryTransmissionPolicyFile _policy = TelemetryTransmissionPolicyDefaults.CreateDefault();
    private bool _policyHealthy;
    private HashSet<string> _relayAllowedKinds = new(StringComparer.Ordinal);
    private string? _relayIngestCap;
    private string _effectiveRouteMode = "direct";

    private static readonly HashSet<string> EmptyRelayKinds = new(StringComparer.Ordinal);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ConfigService _config;
    private readonly ILogger<TelemetryTransmissionPolicyBackgroundService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public TelemetryTransmissionPolicyBackgroundService(
        IHttpClientFactory httpFactory,
        ConfigService config,
        ILogger<TelemetryTransmissionPolicyBackgroundService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public TelemetryTransmissionPolicyFile GetCurrentPolicy()
    {
        lock (_policyLock)
            return _policy;
    }

    public bool IsTelemetryPolicyHealthy
    {
        get
        {
            lock (_policyLock)
                return _policyHealthy;
        }
    }

    public IReadOnlySet<string> RelayAllowedEventKinds
    {
        get
        {
            lock (_policyLock)
                return _policyHealthy ? _relayAllowedKinds : EmptyRelayKinds;
        }
    }

    public string? RelayIngestLogLevelCap
    {
        get
        {
            lock (_policyLock)
                return _policyHealthy ? _relayIngestCap : null;
        }
    }

    public string EffectiveRouteMode
    {
        get
        {
            lock (_policyLock)
                return _effectiveRouteMode;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnConfigChanged()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Telemetry policy refresh on config change failed");
                }
            }, stoppingToken);
        }

        _config.OnConfigChanged += OnConfigChanged;
        try
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _config.OnConfigChanged -= OnConfigChanged;
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var apiKey = (cfg.AiGatewayApiKey ?? "").Trim();
        var baseUrl = TelemetryRelayDefaults.GetEffectiveRelayBaseUrl(cfg);
        if (!cfg.TelemetryEnabled || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            lock (_policyLock)
            {
                _policy = TelemetryTransmissionPolicyDefaults.CreateDefault();
                _policyHealthy = false;
                _relayAllowedKinds = new HashSet<string>(StringComparer.Ordinal);
                _relayIngestCap = null;
                _effectiveRouteMode = "direct";
            }

            return;
        }

        try
        {
            var aggUrl = $"{baseUrl}/api/policy/aggregated";
            var profileId = (cfg.OpsPolicyProfileId ?? "").Trim();
            if (!string.IsNullOrEmpty(profileId))
                aggUrl += "?profileId=" + Uri.EscapeDataString(profileId);
            using var req = new HttpRequestMessage(HttpMethod.Get, aggUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var client = _httpFactory.CreateClient("telemetry");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("Telemetry aggregated policy GET status={Status}", res.StatusCode);
                MarkUnhealthy();
                return;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var envelope = await JsonSerializer.DeserializeAsync<AggregatedPolicyEnvelope>(stream, JsonOpts, ct).ConfigureAwait(false);
            var agg = envelope?.Effective;
            if (agg is null)
            {
                MarkUnhealthy();
                return;
            }

            var emissionAllowed = agg.TelemetryEmissionAllowed != false;
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            if (agg.AvailableEventKinds is { Count: > 0 })
            {
                foreach (var e in agg.AvailableEventKinds)
                {
                    var k = (e.Kind ?? "").Trim();
                    if (k.Length > 0)
                        kinds.Add(k);
                }
            }

            var healthy = emissionAllowed && kinds.Count > 0;
            var mergedTransmission = agg.Transmission != null
                ? TelemetryTransmissionPolicyDefaults.Merge(agg.Transmission)
                : TelemetryTransmissionPolicyDefaults.CreateDefault();
            var ingestCap = (agg.IngestLogLevel ?? "").Trim();
            if (string.IsNullOrEmpty(ingestCap))
                ingestCap = "information";

            var routeMode = (agg.RouteMode ?? "gateway").Trim().ToLowerInvariant();
            if (routeMode != "gateway" && routeMode != "direct") routeMode = "gateway";

            lock (_policyLock)
            {
                if (healthy)
                {
                    _policyHealthy = true;
                    _relayAllowedKinds = kinds;
                    _policy = mergedTransmission;
                    _relayIngestCap = ingestCap;
                    _effectiveRouteMode = routeMode;
                }
                else
                {
                    _policyHealthy = false;
                    _relayAllowedKinds = new HashSet<string>(StringComparer.Ordinal);
                    _policy = TelemetryTransmissionPolicyDefaults.CreateDefault();
                    _relayIngestCap = null;
                    _effectiveRouteMode = "direct";
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry transmission policy refresh failed");
            MarkUnhealthy();
        }
    }

    private void MarkUnhealthy()
    {
        lock (_policyLock)
        {
            _policyHealthy = false;
            _relayAllowedKinds = new HashSet<string>(StringComparer.Ordinal);
            _policy = TelemetryTransmissionPolicyDefaults.CreateDefault();
            _relayIngestCap = null;
            _effectiveRouteMode = "direct";
        }
    }
}
