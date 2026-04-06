using System.Diagnostics;

namespace OfficeCopilot.Server.Diagnostics;

/// <summary>MAF/主会话/编排的 <see cref="ActivitySource"/>；可接 OpenTelemetry 或任意 <see cref="ActivityListener"/>。</summary>
public static class MafActivitySource
{
    public static readonly ActivitySource Activity = new("OfficeCopilot.Maf", "1.0.0");
}
