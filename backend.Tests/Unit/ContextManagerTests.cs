using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ContextManagerTests
{
    /// <summary>当 PassThroughContext = true 时，TrimHistory 只按轮数裁、不按 token 裁。</summary>
    [Fact]
    public void TrimHistory_PassThroughContextTrue_OnlyTrimsByTurnsNotByTokenBudget()
    {
        var configService = CreateConfigServiceWithPassThrough(maxHistoryTurns: 2);
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextManager>();
        var manager = new ContextManager(configService, logger);

        var history = new ChatHistory("system");
        for (var i = 0; i < 5; i++)
        {
            history.AddUserMessage("user " + i);
            history.AddAssistantMessage(new string('x', 5000));
        }
        Assert.True(history.Count > 5);

        manager.TrimHistory(history, activeEntry: null);

        var maxMessagesByTurns = 1 + 2 * 2;
        Assert.Equal(maxMessagesByTurns, history.Count);
    }

    private static ConfigService CreateConfigServiceWithPassThrough(int maxHistoryTurns)
    {
        var configBuilder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>());
        var configuration = configBuilder.Build();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));
        var configLogger = loggerFactory.CreateLogger<ConfigService>();
        var configService = new ConfigService(configuration, configLogger);

        var appConfig = new AppConfig
        {
            Session = new SessionConfig
            {
                MaxHistoryTurns = maxHistoryTurns,
                MinTurnsToKeep = 1,
                TimeoutMinutes = 30,
                CleanupIntervalMinutes = 5
            },
            ContextWindow = new ContextWindowConfig
            {
                PassThroughContext = true,
                MaxContextTokens = 64_000,
                ReservedOutputTokens = 4_096
            }
        };
        var field = typeof(ConfigService).GetField("_currentConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(configService, appConfig);

        return configService;
    }
}
