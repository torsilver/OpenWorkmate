using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Configuration;

namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>
/// 从内存队列取出观测事件，经 <see cref="ILogger"/> 结构化写入（Serilog → Seq，由 <c>Telemetry:SeqServerUrl</c> 配置）。
/// 不再 POST 到中继 <c>/ingest/batch</c>。
/// </summary>
public sealed class TelemetryRelayDispatchService : BackgroundService
{
    private static int s_seqSkipInfoLogged;

    private static readonly JsonSerializerOptions PayloadJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly TelemetryRelayQueue _queue;
    private readonly ConfigService _configService;
    private readonly IConfiguration _configuration;
    private readonly ITelemetryTransmissionPolicyProvider _telemetryPolicy;
    private readonly ILogger<TelemetryRelayDispatchService> _logger;

    public TelemetryRelayDispatchService(
        TelemetryRelayQueue queue,
        ConfigService configService,
        IConfiguration configuration,
        ITelemetryTransmissionPolicyProvider telemetryPolicy,
        ILogger<TelemetryRelayDispatchService> logger)
    {
        _queue = queue;
        _configService = configService;
        _configuration = configuration;
        _telemetryPolicy = telemetryPolicy;
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
                FlushToSeq(buffer);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry flush to Seq failed (count={Count})", buffer.Count);
            }
        }
    }

    private void FlushToSeq(List<TelemetryRelayEvent> buffer)
    {
        var cfg = _configService.Current;
        var seqUrl = (_configuration["Telemetry:SeqServerUrl"] ?? "").Trim();
        if (!cfg.TelemetryEnabled || string.IsNullOrEmpty(seqUrl))
        {
            if (Interlocked.CompareExchange(ref s_seqSkipInfoLogged, 1, 0) == 0)
            {
                _logger.LogInformation(
                    "Telemetry emission skipped: TelemetryEnabled={Enabled}, SeqServerUrl configured={SeqConfigured}",
                    cfg.TelemetryEnabled,
                    !string.IsNullOrEmpty(seqUrl));
            }
            return;
        }

        if (!_telemetryPolicy.IsTelemetryPolicyHealthy)
            return;

        foreach (var e in buffer)
            EmitOne(e);
    }

    private void EmitOne(TelemetryRelayEvent e)
    {
        string? payloadStr = null;
        if (e.Payload is { } pe && pe.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            payloadStr = JsonSerializer.Serialize(pe, PayloadJsonOpts);

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["TelemetryDeviceId"] = e.DeviceId,
            ["TelemetrySessionId"] = e.SessionId,
            ["TelemetryEventType"] = e.EventType,
            ["TelemetryDetailLevel"] = e.DetailLevel,
            ["TelemetryClientTier"] = e.ClientTier,
            ["TelemetryClientType"] = e.ClientType,
            ["TelemetryModelId"] = e.ModelId,
            ["TelemetryTimestampUtc"] = (e.TimestampUtc ?? DateTime.UtcNow).ToString("O"),
            ["TelemetryPayloadJson"] = payloadStr
        }))
        {
            _logger.LogInformation(
                "Telemetry {EventType} session={SessionId} tier={Tier}",
                e.EventType,
                e.SessionId,
                e.ClientTier);
        }
    }
}
