using System.Net;
using OfficeCopilot.Server.Security;
using Serilog;

namespace OfficeCopilot.Server;

/// <summary>未显式配置 urls / ASPNETCORE_URLS 时，按 WebSocket:Port 起算做本机端口回退并 UseUrls。</summary>
internal static class LocalListenUrlConfigurer
{
    public static void Apply(WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        if (!string.IsNullOrWhiteSpace(cfg["urls"]))
            return;

        var ws = cfg.GetSection("WebSocket");
        var portStr = ws["Port"] ?? "8765";
        var basePort = 8765;
        if (int.TryParse(portStr, out var bp) && bp is >= 1 and <= 65535)
            basePort = bp;

        var countStr = ws["PortFallbackCount"] ?? "10";
        var tryCount = 10;
        if (int.TryParse(countStr, out var tc) && tc is >= 1 and <= 100)
            tryCount = tc;

        LocalServiceListenOptions.PortScanStart = basePort;
        LocalServiceListenOptions.PortScanCount = tryCount;

        var chosen = LocalListenPortSelector.TryFindFirstAvailablePort(IPAddress.Loopback, basePort, tryCount);
        if (chosen < 0)
        {
            StartupTrace.Write(
                $"LocalListenUrlConfigurer: no free port in {basePort}..{basePort + tryCount - 1} on loopback; exiting.");
            Log.Fatal(
                "无法在 {Start}..{End} 内绑定本机监听端口。请关闭占用进程、在 appsettings 中调整 WebSocket:Port / PortFallbackCount，或通过环境变量 ASPNETCORE_URLS 指定监听地址。",
                basePort, basePort + tryCount - 1);
            Environment.Exit(1);
        }

        var listenUrl = $"http://127.0.0.1:{chosen}";
        builder.WebHost.UseUrls(listenUrl);
        LocalServiceListenOptions.ListenBaseUrl = listenUrl.TrimEnd('/');
        LocalServiceListenOptions.UsedPortFallback = true;
    }
}
