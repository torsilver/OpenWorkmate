using System.Text.Json;

using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Telemetry;

public static class TelemetryRelaySessionExtensions
{
    public static void TryEnqueueFromSession(
        this ITelemetryRelayQueue? queue,
        ConfigService config,
        ITelemetryTransmissionPolicyProvider policyProvider,
        SessionManager sessions,
        string? sessionId,
        string eventType,
        string detailLevel,
        string? message,
        string? modelId = null,
        JsonElement? payload = null,
        DateTime? timestampUtc = null)
    {
        if (queue is null || string.IsNullOrEmpty(sessionId)) return;
        if (config.Current.TelemetryUserObservabilityEnabled == false)
            return;
        var deviceId = sessions.GetTelemetryDeviceId(sessionId);
        if (string.IsNullOrEmpty(deviceId) || !Guid.TryParse(deviceId, out _))
            return;
        var tier = sessions.GetTelemetryTier(sessionId) ?? "minimal";
        if (string.Equals(tier, "off", StringComparison.OrdinalIgnoreCase))
            return;
        var ingestLv = sessions.GetTelemetryIngestLogLevel(sessionId);
        if (!TelemetryIngestDetailGate.AllowsEnqueueCombined(ingestLv, policyProvider.RelayIngestLogLevelCap, detailLevel))
            return;
        if (!policyProvider.IsTelemetryPolicyHealthy)
            return;
        var relayAllowed = policyProvider.RelayAllowedLogKinds;
        var sessionKinds = sessions.GetTelemetryLogKinds(sessionId);
        var effective = TelemetryEffectiveLogKinds.Compute(relayAllowed, sessionKinds);
        if (!effective.Contains(eventType))
            return;
        var policy = policyProvider.GetCurrentPolicy();
        var (msgOut, payloadOut) = TelemetryOutboundRedactor.Apply(tier, eventType, message, payload, policy);
        queue.TryEnqueue(new TelemetryRelayEvent
        {
            DeviceId = deviceId,
            ClientTier = tier,
            SessionId = sessionId,
            EventType = eventType,
            DetailLevel = detailLevel,
            ClientType = sessions.GetClientType(sessionId),
            ModelId = modelId,
            Message = msgOut,
            Payload = payloadOut,
            TimestampUtc = timestampUtc ?? DateTime.UtcNow
        });
    }
}
