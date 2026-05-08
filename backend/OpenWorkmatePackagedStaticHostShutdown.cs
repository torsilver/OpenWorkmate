#if WINDOWS
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server;

/// <summary>
/// MSI / stage 布局下 StaticHost 常由 Start-OpenWorkmate.ps1 与后台并列启动；托盘退出只停本进程，需主动结束同目录下的 OpenWorkmate.StaticHost。
/// </summary>
internal static class OpenWorkmatePackagedStaticHostShutdown
{
    public static void Register(WebApplication app)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("OpenWorkmatePackagedStaticHost");
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopped.Register(() =>
        {
            try
            {
                StopSiblingStaticHostIfPackagedLayout(logger);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Stopping OpenWorkmate.StaticHost sibling failed (ignored).");
            }
        });
    }

    private static void StopSiblingStaticHostIfPackagedLayout(ILogger? logger)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installRoot = Directory.GetParent(baseDir)?.FullName;
        if (string.IsNullOrEmpty(installRoot))
            return;

        var staticHostExe = Path.Combine(installRoot, "OpenWorkmate.StaticHost", "OpenWorkmate.StaticHost.exe");
        if (!File.Exists(staticHostExe))
            return;

        var target = Path.GetFullPath(staticHostExe);

        foreach (var proc in Process.GetProcessesByName("OpenWorkmate.StaticHost"))
        {
            using (proc)
            {
                string? path;
                try
                {
                    path = proc.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(path))
                    continue;

                if (!string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    logger?.LogInformation("Stopping packaged OpenWorkmate.StaticHost (pid {Pid}).", proc.Id);
                    proc.Kill(entireProcessTree: false);
                    proc.WaitForExit(milliseconds: 8000);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Kill OpenWorkmate.StaticHost pid {Pid} skipped.", proc.Id);
                }
            }
        }
    }
}
#endif
