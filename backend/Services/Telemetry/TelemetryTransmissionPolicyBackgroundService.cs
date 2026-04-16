using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Telemetry;

public interface ITelemetryTransmissionPolicyProvider
{
    /// <summary>当前缓存的传输策略；持久化仍以中继 ingest 为准。</summary>
    TelemetryTransmissionPolicyFile GetCurrentPolicy();
}

/// <summary>
/// 从遥测中继 <c>GET /policy/transmission</c> 拉取策略并定时刷新（默认 30s + 配置变更）；失败保留上次或内置默认。
/// 落盘仍以中继 ingest 为准；本缓存仅影响 AI 出站裁剪，中继策略更新后最多滞后至下一刷新周期。
/// </summary>
public sealed class TelemetryTransmissionPolicyBackgroundService : BackgroundService, ITelemetryTransmissionPolicyProvider
{
    private TelemetryTransmissionPolicyFile _policy = TelemetryTransmissionPolicyDefaults.CreateDefault();
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

    public TelemetryTransmissionPolicyFile GetCurrentPolicy() => _policy;

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
        var apiKey = (cfg.TelemetryRelayApiKey ?? "").Trim();
        var baseUrl = TelemetryRelayDefaults.GetEffectiveRelayBaseUrl(cfg);
        if (!cfg.TelemetryEnabled || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            _policy = TelemetryTransmissionPolicyDefaults.CreateDefault();
            return;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/policy/transmission");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var client = _httpFactory.CreateClient("telemetry");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("Telemetry transmission policy GET status={Status}", res.StatusCode);
                return;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var parsed = await JsonSerializer.DeserializeAsync<TelemetryTransmissionPolicyFile>(stream, JsonOpts, ct).ConfigureAwait(false);
            if (parsed != null)
                _policy = TelemetryTransmissionPolicyDefaults.Merge(parsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry transmission policy refresh failed");
        }
    }
}
