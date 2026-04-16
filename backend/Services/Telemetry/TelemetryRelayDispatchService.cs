using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>从内存队列批量 POST 到遥测中继 <c>/ingest/batch</c>；失败仅打日志，不影响主业务。</summary>
public sealed class TelemetryRelayDispatchService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TelemetryRelayQueue _queue;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigService _configService;
    private readonly ILogger<TelemetryRelayDispatchService> _logger;

    public TelemetryRelayDispatchService(
        TelemetryRelayQueue queue,
        IHttpClientFactory httpClientFactory,
        ConfigService configService,
        ILogger<TelemetryRelayDispatchService> logger)
    {
        _queue = queue;
        _httpClientFactory = httpClientFactory;
        _configService = configService;
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
                await FlushBatchAsync(buffer, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry batch flush failed (count={Count})", buffer.Count);
            }
        }
    }

    private async Task FlushBatchAsync(List<TelemetryRelayEvent> buffer, CancellationToken ct)
    {
        var cfg = _configService.Current;
        if (!cfg.TelemetryEnabled) return;
        var baseUrl = (cfg.TelemetryRelayBaseUrl ?? "").Trim().TrimEnd('/');
        var apiKey = (cfg.TelemetryRelayApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey)) return;

        foreach (var group in buffer.GroupBy(e => (e.DeviceId, e.ClientTier)))
        {
            var wireEvents = new List<TelemetryIngestEventWire>(group.Count());
            foreach (var e in group)
            {
                JsonElement? payloadEl = null;
                if (e.Payload is { } pe
                    && pe.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                    payloadEl = pe;

                wireEvents.Add(new TelemetryIngestEventWire
                {
                    SessionId = e.SessionId,
                    EventType = e.EventType,
                    TimestampUtc = e.TimestampUtc ?? DateTime.UtcNow,
                    DetailLevel = e.DetailLevel,
                    ClientType = e.ClientType,
                    ModelId = e.ModelId,
                    Message = e.Message,
                    Payload = payloadEl
                });
            }

            var payload = new
            {
                deviceId = group.Key.DeviceId,
                clientTier = group.Key.ClientTier,
                events = wireEvents
            };
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var client = _httpClientFactory.CreateClient("telemetry");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/ingest/batch")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            try
            {
                var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    _logger.LogDebug("Telemetry relay HTTP {Code}", (int)res.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry relay POST failed");
            }
        }
    }
}

/// <summary>与遥测中继 <c>/ingest/batch</c> 事件项字段对齐（camelCase）。</summary>
internal sealed class TelemetryIngestEventWire
{
    public string SessionId { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string DetailLevel { get; set; } = "p0";
    public string? ClientType { get; set; }
    public string? ModelId { get; set; }
    public string? Message { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; set; }
}
