using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using OfficeCopilot.Server;

namespace backend.Tests.Integration;

/// <summary>集成测试在启动宿主前写入临时 user-config.json（应用配置仅此来源）。</summary>
internal static class IntegrationTestUserConfigWriter
{
    public static void Write(string path, string scheduledTasksDir, string? webSocketAuthToken)
    {
        var cfg = new AppConfig
        {
            AI = new AiConfig(),
            RagStorageType = "Memory",
            PlansDirectory = "",
            ScheduledTasksDirectory = scheduledTasksDir,
            WebSocketAuthToken = webSocketAuthToken ?? "",
        };
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = JsonCtx.Default
        };
        var json = JsonSerializer.Serialize(cfg, typeof(AppConfig), options);
        File.WriteAllText(path, json);
    }
}
