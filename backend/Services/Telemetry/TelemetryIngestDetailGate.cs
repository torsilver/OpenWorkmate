namespace OpenWorkmate.Server.Services.Telemetry;

/// <summary>按 Chrome 选项「遥测详细程度 / 日志档位」限制出站 detailLevel（p0/p1/p2），与中继策略独立。</summary>
public static class TelemetryIngestDetailGate
{
    public static bool AllowsEnqueue(string? ingestLogLevel, string detailLevel)
    {
        var cap = MaxDetailRank(ingestLogLevel);
        if (cap < 0) return false;
        return DetailRank(detailLevel) <= cap;
    }

    /// <summary>会话侧与中继侧（聚合策略 profile）取更严的 detail 上限；任一侧为 off 则拒绝。</summary>
    public static bool AllowsEnqueueCombined(string? sessionIngestLogLevel, string? relayIngestCap, string detailLevel)
    {
        var s = MaxDetailRank(sessionIngestLogLevel);
        if (s < 0) return false;
        if (string.IsNullOrWhiteSpace(relayIngestCap))
            return DetailRank(detailLevel) <= s;
        var r = MaxDetailRank(relayIngestCap);
        if (r < 0) return false;
        var cap = Math.Min(s, r);
        return DetailRank(detailLevel) <= cap;
    }

    private static int DetailRank(string? detailLevel)
    {
        var s = (detailLevel ?? "p0").Trim().ToLowerInvariant();
        return s switch
        {
            "p2" or "full" => 2,
            "p1" => 1,
            _ => 0
        };
    }

    private static int MaxDetailRank(string? ingestLogLevel)
    {
        var s = (ingestLogLevel ?? "information").Trim().ToLowerInvariant();
        return s switch
        {
            "off" or "none" => -1,
            "error" => 0,
            "warning" => 1,
            "information" or "info" => 1,
            "debug" or "verbose" => 2,
            _ => 1
        };
    }
}
