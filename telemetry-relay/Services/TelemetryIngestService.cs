using Microsoft.Extensions.Options;
using Taskly.Telemetry.Relay.Models;

namespace Taskly.Telemetry.Relay.Services;

public sealed class TelemetryIngestService
{
    private readonly TelemetryPolicyResolver _policy;
    private readonly TelemetrySessionWriter _writer;
    private readonly IOptionsMonitor<TelemetryOptions> _opt;
    private readonly ILogger<TelemetryIngestService> _logger;
    public TelemetryIngestService(
        TelemetryPolicyResolver policy,
        TelemetrySessionWriter writer,
        IOptionsMonitor<TelemetryOptions> opt,
        ILogger<TelemetryIngestService> logger)
    {
        _policy = policy;
        _writer = writer;
        _opt = opt;
        _logger = logger;
    }

    public async Task<(int Accepted, int Skipped)> IngestAsync(IngestBatchRequest batch, CancellationToken ct)
    {
        if (batch.Events is not { Count: > 0 }) return (0, 0);
        var deviceId = batch.DeviceId?.Trim() ?? "";
        if (!TelemetryPathValidator.IsValidDeviceId(deviceId))
            throw new ArgumentException("Invalid deviceId (expected GUID).");

        var policy = _policy.ResolveForDevice(deviceId, batch.ClientTier);
        if (policy.EffectiveTier == TelemetryTier.Off)
            return (0, batch.Events.Count);

        var maxChars = Math.Clamp(_opt.CurrentValue.MaxEventPayloadChars, 1000, 500_000);
        var rnd = Random.Shared;
        var accepted = 0;
        var skipped = 0;
        foreach (var ev in batch.Events)
        {
            if (ev is null) { skipped++; continue; }
            var sid = ev.SessionId?.Trim() ?? "";
            if (!TelemetryPathValidator.IsValidSessionFileKey(sid))
            {
                skipped++;
                continue;
            }

            var line = TelemetryLineFormatter.FormatLine(ev, policy, maxChars, rnd);
            if (string.IsNullOrEmpty(line))
            {
                skipped++;
                continue;
            }

            try
            {
                await _writer.AppendLineAsync(deviceId, sid, line, ct).ConfigureAwait(false);
                accepted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Append failed device={Device} session={Session}", deviceId, sid);
                skipped++;
            }
        }

        return (accepted, skipped);
    }
}
