using System.Collections.Generic;
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
            AiModels =
            [
                new AiModelEntry
                {
                    Id = "default",
                    DisplayName = "默认模型",
                    Enabled = true,
                    Provider = "OpenAI",
                    Endpoint = "https://api.openai.com/v1",
                    ModelId = "gpt-4o-mini"
                }
            ],
            ActiveModelId = "default",
            AlwaysIncludePlugins = new List<string>(),
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
