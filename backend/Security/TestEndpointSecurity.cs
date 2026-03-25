using System.Net;
using System.Net.Sockets;

namespace OfficeCopilot.Server.Security;

/// <summary>限制「测试连接」类接口的服务端出站目标，降低 SSRF 风险。</summary>
public static class TestEndpointSecurity
{
    /// <summary>若不应允许向该 URI 发起探测请求，返回中文原因；否则 null。</summary>
    public static string? GetBlockedReason(Uri uri, bool allowPrivateEndpoints)
    {
        if (allowPrivateEndpoints) return null;
        if (!uri.IsAbsoluteUri) return "接口地址无效。";
        if (uri.Scheme != "http" && uri.Scheme != "https") return "仅允许 http(s) 地址。";

        var host = uri.IdnHost;
        if (string.IsNullOrEmpty(host)) return "主机名无效。";

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return "为安全起见，测试连接不允许指向 localhost；请使用公网可解析的 API 地址，或在设置中开启「允许内网/回环测试」。";

        if (string.Equals(host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "metadata.goog", StringComparison.OrdinalIgnoreCase))
            return "为安全起见，不允许向云元数据主机发起测试连接。";

        if (!IPAddress.TryParse(host, out var ip))
            return null;

        return GetBlockedReasonForIp(ip);
    }

    public static string? GetBlockedReasonForIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return "为安全起见，测试连接不允许指向回环地址；请使用公网可解析的 API 地址，或在设置中开启「允许内网/回环测试」。";

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return BlockedPrivateMessage;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return BlockedPrivateMessage;
            if (b[0] == 192 && b[1] == 168) return BlockedPrivateMessage;
            if (b[0] == 169 && b[1] == 254) return BlockedPrivateMessage;
            if (b[0] == 127) return BlockedPrivateMessage;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return BlockedPrivateMessage;
            // Unique local fc00::/7
            var s = ip.ToString();
            if (s.StartsWith("fc", StringComparison.OrdinalIgnoreCase) || s.StartsWith("fd", StringComparison.OrdinalIgnoreCase))
                return BlockedPrivateMessage;
        }

        return null;
    }

    private const string BlockedPrivateMessage =
        "为安全起见，测试连接不允许指向内网/链路本地地址；请使用公网可解析的 API 地址，或在设置中开启「允许内网/回环测试」。";
}
