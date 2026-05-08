using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenWorkmate.Server;
using Xunit;

namespace backend.Tests.Unit;

public class ConfigServiceCliRunModeTests
{
    [Fact]
    public void GetCliRunModeForEnd_BackendAskEverytime_ReturnsUseAllowList()
    {
        var configService = CreateConfigServiceWithCliRunMode("AskEverytime");

        Assert.Equal("UseAllowList", configService.GetCliRunModeForEnd(CliScriptEndKeys.Backend));
    }

    [Fact]
    public void GetCliRunModeForEnd_ChromeAskEverytime_Unchanged()
    {
        var configService = CreateConfigServiceWithCliRunMode("AskEverytime");

        Assert.Equal("AskEverytime", configService.GetCliRunModeForEnd(CliScriptEndKeys.Chrome));
    }

    [Fact]
    public void GetCliRunModeForEnd_BackendRunEverything_Unchanged()
    {
        var configService = CreateConfigServiceWithCliRunMode("RunEverything");

        Assert.Equal("RunEverything", configService.GetCliRunModeForEnd(CliScriptEndKeys.Backend));
    }

    [Fact]
    public void GetCliRunModeForEnd_AllEndsShareGlobalMode()
    {
        var configService = CreateConfigServiceWithCliRunMode("UseAllowList");

        Assert.Equal("UseAllowList", configService.GetCliRunModeForEnd(CliScriptEndKeys.Chrome));
        Assert.Equal("UseAllowList", configService.GetCliRunModeForEnd(CliScriptEndKeys.Office));
    }

    private static ConfigService CreateConfigServiceWithCliRunMode(string cliRunMode)
    {
        var configBuilder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>());
        var configuration = configBuilder.Build();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));
        var configLogger = loggerFactory.CreateLogger<ConfigService>();
        var configService = new ConfigService(configuration, configLogger);

        var appConfig = new AppConfig
        {
            CliRunMode = cliRunMode
        };
        var field = typeof(ConfigService).GetField("_currentConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(configService, appConfig);

        return configService;
    }
}
