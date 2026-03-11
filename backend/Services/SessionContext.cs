namespace OfficeCopilot.Server.Services;

/// <summary>
/// Holds the current request's session ID across async flow so that
/// plugins (e.g. BrowserPlugin) can access it when invoked by the kernel.
/// </summary>
public static class SessionContext
{
    private static readonly AsyncLocal<string?> CurrentSessionId = new();

    public static void SetSessionId(string? sessionId) => CurrentSessionId.Value = sessionId;

    public static string? GetSessionId() => CurrentSessionId.Value;
}
