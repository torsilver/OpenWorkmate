namespace OpenWorkmate.Server.Services.Telemetry;

/// <summary>Gateway <c>availableEventKinds</c> 与 WebSocket <c>telemetryEventKinds</c> 的交集；用户未选时等于 Gateway 全集。</summary>
public static class TelemetryEffectiveEventKinds
{
    /// <param name="relayAllowed">非空；由 <see cref="ITelemetryTransmissionPolicyProvider.RelayAllowedEventKinds"/> 在 healthy 时提供。</param>
    /// <param name="sessionFilter">会话 query 解析结果；null 或空表示「在 Gateway 允许范围内全选」。</param>
    public static HashSet<string> Compute(IReadOnlySet<string> relayAllowed, HashSet<string>? sessionFilter)
    {
        if (relayAllowed.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        if (sessionFilter is not { Count: > 0 })
            return new HashSet<string>(relayAllowed, StringComparer.Ordinal);

        var eff = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in sessionFilter)
        {
            if (relayAllowed.Contains(k))
                eff.Add(k);
        }

        return eff;
    }
}
