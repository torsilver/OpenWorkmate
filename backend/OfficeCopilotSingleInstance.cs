using System.Threading;

namespace OfficeCopilot.Server;

/// <summary>Windows 下全局单实例：与托盘/控制台共用同一 Mutex，先于端口绑定与 WebHost 构建。</summary>
internal static class OfficeCopilotSingleInstance
{
    private static Mutex? _mutex;

    /// <summary>尝试成为本机唯一实例；若已有实例在运行则返回 false。</summary>
    public static bool TryAcquire()
    {
        const string name = @"Local\OfficeCopilot.Server.SingleInstance";
        Mutex m;
        bool createdNew;
        try
        {
            m = new Mutex(true, name, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (!createdNew)
        {
            m.Dispose();
            return false;
        }

        _mutex = m;
        return true;
    }
}
