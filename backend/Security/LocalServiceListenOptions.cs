namespace OfficeCopilot.Server.Security;

/// <summary>由 Program 在解析监听 URL 后写入，供引导接口与发现文件使用。</summary>
public static class LocalServiceListenOptions
{
    public static string? ListenBaseUrl { get; set; }

    /// <summary>本次启动用于回退扫描的起始端口（配置项 WebSocket:Port）。</summary>
    public static int PortScanStart { get; set; } = 8765;

    /// <summary>本次启动尝试的端口个数（含起始端口）。</summary>
    public static int PortScanCount { get; set; } = 10;

    /// <summary>是否使用了「无显式 urls 时的端口回退」逻辑（用于决定是否写发现文件等）。</summary>
    public static bool UsedPortFallback { get; set; }

    public static void Reset()
    {
        ListenBaseUrl = null;
        PortScanStart = 8765;
        PortScanCount = 10;
        UsedPortFallback = false;
    }
}
