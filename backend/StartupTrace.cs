namespace OfficeCopilot.Server;

/// <summary>
/// 最早阶段的纯文件追踪（不依赖 Serilog），用于 MSI/Program Files 等场景下定位「进程是否启动、卡在哪一步」。
/// 写入 %LocalAppData%\OfficeCopilot\startup-trace.txt。
/// </summary>
internal static class StartupTrace
{
    public static void Write(string message)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OfficeCopilot");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "startup-trace.txt");
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            /* 绝不因诊断写入失败再抛异常 */
        }
    }
}
