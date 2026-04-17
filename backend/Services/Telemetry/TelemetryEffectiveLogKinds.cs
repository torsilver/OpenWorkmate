namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>远端 <c>availableLogKinds</c> 与 WebSocket <c>telemetryLogKinds</c> 的交集；用户未选时等于 relay 全集。</summary>
public static class TelemetryEffectiveLogKinds
{
    /// <param name="relayAllowed">非空；由 <see cref="ITelemetryTransmissionPolicyProvider.RelayAllowedLogKinds"/> 在 healthy 时提供。</param>
    /// <param name="sessionFilter">会话 query 解析结果；null 或空表示「在 relay 允许范围内全选」。</param>
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
