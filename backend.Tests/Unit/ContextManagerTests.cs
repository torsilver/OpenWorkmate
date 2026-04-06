using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ContextManagerTests
{
    [Fact]
    public void TrimHistory_PassThroughContextTrue_OnlyTrimsByTurnsNotByTokenBudget()
    {
        var configService = CreateConfigServiceWithPassThrough(maxHistoryTurns: 2);
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextManager>();
        var manager = new ContextManager(configService, logger);

        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        for (var i = 0; i < 5; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, "user " + i));
            history.Add(new ChatMessage(ChatRole.Assistant, new string('x', 5000)));
        }
        Assert.True(history.Count > 5);

        manager.TrimHistory(history, activeEntry: null);

        var maxMessagesByTurns = 1 + 2 * 2;
        Assert.Equal(maxMessagesByTurns, history.Count);
    }

    [Fact]
    public void TrimHistory_WithCompactBoundary_DoesNotRemoveSummaryOrAnchor()
    {
        var configService = CreateConfigServiceWithPassThrough(maxHistoryTurns: 1);
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextManager>();
        var manager = new ContextManager(configService, logger);

        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        var summary = ConversationCompactBoundary.BuildSummaryMessageBody("summarized", DateTimeOffset.UtcNow);
        history.Add(new ChatMessage(ChatRole.User, summary));
        history.Add(new ChatMessage(ChatRole.Assistant, "after-compact-anchor"));
        for (var i = 0; i < 4; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, "user " + i));
            history.Add(new ChatMessage(ChatRole.Assistant, "asst " + i));
        }

        manager.TrimHistory(history, activeEntry: null);

        Assert.True(history.Count >= 3);
        Assert.Contains(ConversationCompactBoundary.BoundaryMarkerPrefix, history[1].Text ?? "", StringComparison.Ordinal);
        Assert.Equal("after-compact-anchor", history[2].Text);
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
