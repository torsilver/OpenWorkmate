using System.Text.Json;

namespace OfficeCopilot.Server.Services.Telemetry;

public static class TelemetryRelaySessionExtensions
{
    public static void TryEnqueueFromSession(
        this ITelemetryRelayQueue? queue,
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
        var deviceId = sessions.GetTelemetryDeviceId(sessionId);
        if (string.IsNullOrEmpty(deviceId) || !Guid.TryParse(deviceId, out _))
            return;
        var tier = sessions.GetTelemetryTier(sessionId) ?? "minimal";
        if (string.Equals(tier, "off", StringComparison.OrdinalIgnoreCase))
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
