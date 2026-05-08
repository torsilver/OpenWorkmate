namespace OpenWorkmate.Server.Security;

/// <summary>本机服务发现文件路径（与 Data/Plans 等共用 OpenWorkmate 目录）。</summary>
public static class LocalServiceDiscoveryPaths
{
    public const string FileName = "local-service.json";

    public static string GetOpenWorkmateDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "OpenWorkmate");
    }

    public static string GetDiscoveryFilePath() =>
        Path.Combine(GetOpenWorkmateDataDirectory(), FileName);
}
