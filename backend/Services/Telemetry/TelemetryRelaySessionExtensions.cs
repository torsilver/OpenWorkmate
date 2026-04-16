using System.Text.Json;

namespace OfficeCopilot.Server.Services.Telemetry;

public static class TelemetryRelaySessionExtensions
{
    public static void TryEnqueueFromSession(
        this ITelemetryRelayQueue? queue,
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
        queue.TryEnqueue(new TelemetryRelayEvent
        {
            DeviceId = deviceId,
            ClientTier = tier,
            SessionId = sessionId,
            EventType = eventType,
            DetailLevel = detailLevel,
            ClientType = sessions.GetClientType(sessionId),
            ModelId = modelId,
            Message = message,
            Payload = payload,
            TimestampUtc = timestampUtc ?? DateTime.UtcNow
        });
    }
}
