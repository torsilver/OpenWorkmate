namespace OfficeCopilot.Server.Security;

/// <summary>本机服务发现文件路径（与 Data/Plans 等共用 OfficeCopilot 目录）。</summary>
public static class LocalServiceDiscoveryPaths
{
    public const string FileName = "local-service.json";

    public static string GetOfficeCopilotDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "OfficeCopilot");
    }

    public static string GetDiscoveryFilePath() =>
        Path.Combine(GetOfficeCopilotDataDirectory(), FileName);
}
