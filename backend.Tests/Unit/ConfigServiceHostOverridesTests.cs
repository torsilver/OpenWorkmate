using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Unit;

public class ConfigServiceHostOverridesTests
{
    [Fact]
    public void LoadConfig_LoadsScheduledTasksDirectoryFromUserConfigJson()
    {
        var scheduledDir = Path.Combine(Path.GetTempPath(), "OfficeCopilot.cfg-st-" + Guid.NewGuid().ToString("N"));
        var userConfigPath = Path.Combine(Path.GetTempPath(), "OfficeCopilot.cfg-user-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cfg = new AppConfig
            {
                AI = new AiConfig(),
                RagStorageType = "Memory",
                PlansDirectory = "",
                ScheduledTasksDirectory = scheduledDir,
            };
            var options = ConfigService.AppConfigDeserializeOptions;
            File.WriteAllText(userConfigPath, JsonSerializer.Serialize(cfg, ConfigService.AppConfigDeserializeOptions));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["OfficeCopilot:UserConfigPath"] = userConfigPath })
                .Build();

            using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));
            var configService = new ConfigService(configuration, loggerFactory.CreateLogger<ConfigService>());

            Assert.Equal(scheduledDir, configService.Current.ScheduledTasksDirectory);
            Assert.Equal("Memory", configService.Current.RagStorageType);
            Assert.Equal("", configService.Current.PlansDirectory);
        }
        finally
        {
            try
            {
                if (File.Exists(userConfigPath)) File.Delete(userConfigPath);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
