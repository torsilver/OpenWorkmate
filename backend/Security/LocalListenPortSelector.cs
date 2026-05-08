using System.Net;
using System.Net.Sockets;

namespace OpenWorkmate.Server.Security;

/// <summary>在指定范围内探测第一个可绑定的 TCP 端口（与 Kestrel 实际绑定之间存在极小竞态窗口，桌面场景可接受）。</summary>
public static class LocalListenPortSelector
{
    public static int TryFindFirstAvailablePort(IPAddress address, int startPort, int tryCount)
    {
        if (tryCount < 1) return -1;
        for (var i = 0; i < tryCount; i++)
        {
            var port = startPort + i;
            if (port < 1 || port > 65535)
                break;
            try
            {
                var listener = new TcpListener(address, port);
                listener.ExclusiveAddressUse = true;
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
                // try next
            }
        }

        return -1;
    }
}
