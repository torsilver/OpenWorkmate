namespace OpenWorkmate.Server.Services;

/// <summary>
/// Holds the current request's session ID across async flow so that
/// plugins (e.g. BrowserPlugin) can access it when invoked by the kernel.
/// </summary>
public static class SessionContext
{
    private static readonly AsyncLocal<string?> CurrentSessionId = new();
    private static readonly AsyncLocal<string?> CurrentRoundId = new();

    public static void SetSessionId(string? sessionId) => CurrentSessionId.Value = sessionId;

    public static string? GetSessionId() => CurrentSessionId.Value;

    /// <summary>当前用户轮次 id（与结构化日志、OpenTelemetry tag 关联）；请求结束应清空。</summary>
    public static void SetRoundId(string? roundId) => CurrentRoundId.Value = roundId;

    public static string? GetRoundId() => CurrentRoundId.Value;
}
