namespace OpenWorkmate.Server.Security;

/// <summary>收紧 CORS：按配置前缀匹配，并始终允许常见扩展与本机调试来源。</summary>
public static class CorsOriginEvaluator
{
    public static bool IsOriginAllowed(string? origin, IReadOnlyList<string> configuredPrefixes, bool isDevelopment)
    {
        if (string.IsNullOrEmpty(origin)) return true;

        foreach (var prefix in configuredPrefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            var p = prefix.Trim();
            if (origin.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (isDevelopment)
        {
            if (origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
            if (origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)) return true;
            // Office 旁加载开发：taskpane 由 https://localhost:PORT 托管（见 office-addin），须放行否则 fetch/ws 报 Failed to fetch
            if (origin.StartsWith("https://127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
            if (origin.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return true;
        if (origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
