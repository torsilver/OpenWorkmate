using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Hosting;

namespace OpenWorkmate.Server.Services.Telemetry;

/// <summary>
/// 从内存队列取出观测事件，批量 POST 到本机 AI Gateway <c>/ingest/spans</c>（JSONL 落盘）。
/// </summary>
public sealed class TelemetryRelayDispatchService : BackgroundService
{
    private static int s_ingestSkipInfoLogged;

    private static readonly JsonSerializerOptions IngestJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly TelemetryRelayQueue _queue;
    private readonly ConfigService _configService;
    private readonly ITelemetryTransmissionPolicyProvider _telemetryPolicy;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TelemetryRelayDispatchService> _logger;

    public TelemetryRelayDispatchService(
        TelemetryRelayQueue queue,
        ConfigService configService,
        ITelemetryTransmissionPolicyProvider telemetryPolicy,
        IHttpClientFactory httpFactory,
        ILogger<TelemetryRelayDispatchService> logger)
    {
        _queue = queue;
        _configService = configService;
        _telemetryPolicy = telemetryPolicy;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var buffer = new List<TelemetryRelayEvent>();
            while (reader.TryRead(out var ev))
            {
                buffer.Add(ev);
                if (buffer.Count >= 48) break;
            }

            if (buffer.Count == 0) continue;

            try
            {
                await FlushToGatewayAsync(buffer, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry flush to AI Gateway failed (count={Count})", buffer.Count);
            }
        }
    }

    private async Task FlushToGatewayAsync(List<TelemetryRelayEvent> buffer, CancellationToken ct)
    {
        var cfg = _configService.Current;
        var baseUrl = TelemetryRelayDefaults.GetEffectiveRelayBaseUrl(cfg);
        var apiKey = (cfg.AiGatewayApiKey ?? "").Trim();
        if (!cfg.TelemetryEnabled || cfg.TelemetryUserObservabilityEnabled == false || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            if (Interlocked.CompareExchange(ref s_ingestSkipInfoLogged, 1, 0) == 0)
            {
                _logger.LogInformation(
                    "Telemetry span ingest skipped: TelemetryEnabled={Enabled}, gatewayBaseConfigured={BaseOk}, apiKeyConfigured={KeyOk}",
                    cfg.TelemetryEnabled,
                    !string.IsNullOrEmpty(baseUrl),
                    !string.IsNullOrEmpty(apiKey));
            }
            return;
        }

        if (!_telemetryPolicy.IsTelemetryPolicyHealthy)
            return;

        var events = new List<SpanIngestDto>(buffer.Count);
        foreach (var e in buffer)
        {
            events.Add(new SpanIngestDto
            {
                SessionId = e.SessionId,
                DeviceId = e.DeviceId,
                ClientTier = e.ClientTier,
                EventType = e.EventType,
                DetailLevel = e.DetailLevel,
                ClientType = e.ClientType,
                ModelId = e.ModelId,
                Message = e.Message,
                TimestampUtc = (e.TimestampUtc ?? DateTime.UtcNow).ToString("O"),
                Payload = e.Payload is { } pe && pe.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
                    ? pe
                    : null
            });
        }

        var json = JsonSerializer.Serialize(new { events }, IngestJsonOpts);
        var ingestUrl = $"{baseUrl.TrimEnd('/')}/ingest/spans";

        using var req = new HttpRequestMessage(HttpMethod.Post, ingestUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var client = _httpFactory.CreateClient("telemetry");
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            _logger.LogDebug("Telemetry span ingest POST status={Status} url={Url}", res.StatusCode, ingestUrl);
    }

    private sealed class SpanIngestDto
    {
        public string SessionId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string ClientTier { get; set; } = "";
        public string EventType { get; set; } = "";
        public string DetailLevel { get; set; } = "";
        public string? ClientType { get; set; }
        public string? ModelId { get; set; }
        public string? Message { get; set; }
        public string TimestampUtc { get; set; } = "";
        public JsonElement? Payload { get; set; }
    }
}
