using System.Net;

namespace OfficeCopilot.Server.Security;

/// <summary>防止在未配置认证时将服务绑定到非本机地址。</summary>
public static class ListenAddressGuard
{
    /// <summary>
    /// 若绑定地址对公网/局域网开放且未设置 <paramref name="authToken"/> 且未传入 <paramref name="allowPublicBind"/>，则返回错误说明。
    /// </summary>
    public static string? GetFatalMessageIfUnsafe(
        IEnumerable<string> urls,
        string authToken,
        bool allowPublicBind)
    {
        if (allowPublicBind) return null;
        if (!string.IsNullOrEmpty(authToken?.Trim())) return null;

        foreach (var raw in urls)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;

            if (IsBindingOpenToNetwork(uri.Host))
            {
                return "当前监听地址 \"" + raw + "\" 可能对局域网或公网开放，但未在 user-config.json 中配置 webSocketAuthToken。"
                       + " 请在 %LocalAppData%\\OfficeCopilot\\user-config.json 中设置强随机 webSocketAuthToken，"
                       + " 或使用启动参数 --allow-public-bind 显式承担风险（不推荐）。";
            }
        }

        return null;
    }

    private static bool IsBindingOpenToNetwork(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (host == "+" || host == "*" || host == "0.0.0.0") return true;
        if (string.Equals(host, "[::]", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(host, "::", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
            // 任意非回环的具体 IP 视为可能暴露
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        // 其它主机名（如计算机名）保守视为可能对网络开放
        return true;
    }
}
