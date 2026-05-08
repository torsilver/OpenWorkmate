namespace OpenWorkmate.Server.Services.Telemetry;

/// <summary>WebSocket <c>telemetryEventKinds</c> 查询参数解析；与 Gateway <c>availableEventKinds</c> 的交集由 <see cref="TelemetryEffectiveEventKinds"/> 在 <see cref="TelemetryRelaySessionExtensions.TryEnqueueFromSession"/> 中计算。</summary>
public static class TelemetrySessionEventKindFilter
{
    /// <summary>逗号分隔或重复 query 键；<c>null</c> 或空集合表示「在 Gateway 允许范围内全选」（非无限制）。</summary>
    public static HashSet<string>? ParseFromQuery(Microsoft.AspNetCore.Http.IQueryCollection query)
    {
        if (!query.TryGetValue("telemetryEventKinds", out var values) || values.Count == 0)
            return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0) set.Add(part);
            }
        }

        return set.Count == 0 ? null : set;
    }
}
