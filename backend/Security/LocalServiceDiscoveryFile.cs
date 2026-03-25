using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;

namespace OfficeCopilot.Server.Security;

public sealed class LocalServiceDiscoveryPayload
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("portScanStart")]
    public int PortScanStart { get; set; }

    [JsonPropertyName("portScanCount")]
    public int PortScanCount { get; set; }
}

/// <summary>将当前监听的 base URL 写入 LocalApplicationData，供排障或非浏览器客户端读取。</summary>
public static class LocalServiceDiscoveryFile
{
    /// <summary>集成测试等使用 TestServer 时不应写入真实发现文件。</summary>
    public static bool ShouldWriteDiscoveryFile(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        var server = app.Services.GetService<IServer>();
        if (server == null) return false;
        return !server.GetType().Name.Contains("TestServer", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void TryWrite(string baseUrl, int processId, int portScanStart, int portScanCount)
    {
        try
        {
            var dir = LocalServiceDiscoveryPaths.GetOfficeCopilotDataDirectory();
            Directory.CreateDirectory(dir);
            var path = LocalServiceDiscoveryPaths.GetDiscoveryFilePath();
            var payload = new LocalServiceDiscoveryPayload
            {
                BaseUrl = baseUrl.TrimEnd('/'),
                ProcessId = processId,
                UpdatedAt = DateTimeOffset.UtcNow,
                PortScanStart = portScanStart,
                PortScanCount = portScanCount
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            /* 发现文件失败不应阻止服务启动；日志由调用方决定 */
        }
    }

    public static void TryDelete()
    {
        try
        {
            var path = LocalServiceDiscoveryPaths.GetDiscoveryFilePath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }
}
