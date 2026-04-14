using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.ToolInvocation;
using Xunit;

namespace backend.Tests.Unit;

public class SecurityPipelineCustomScriptRunEverythingTests
{
    [Fact]
    public async Task RunCustomPageScript_RunEverything_ReturnsNullWithoutHitl()
    {
        var configService = CreateConfigServiceWithCliRunMode("RunEverything");
        var sessionManager = new SessionManager(NullLogger<SessionManager>.Instance);
        var hitl = new HitlManager(sessionManager, configService, NullLogger<HitlManager>.Instance);
        var pipeline = new SecurityPipeline(
            NullLogger<SecurityPipeline>.Instance,
            configService,
            hitl,
            sessionManager,
            new NoOpHitlPlainLanguage());

        SessionContext.SetSessionId("any-session");
        try
        {
            var args = new Dictionary<string, object?> { ["scriptCode"] = "return 1;" };
            var result = await pipeline.EvaluateAsync("Browser", "run_custom_javascript_in_page", args, default);
            Assert.Null(result);
        }
        finally
        {
            SessionContext.SetSessionId(null);
        }
    }

    [Fact]
    public async Task RunCustomPageScript_UseAllowList_EmptySession_ReturnsBlockingMessageWithoutHitlWait()
    {
        var configService = CreateConfigServiceWithCliRunMode("UseAllowList");
        var sessionManager = new SessionManager(NullLogger<SessionManager>.Instance);
        var hitl = new HitlManager(sessionManager, configService, NullLogger<HitlManager>.Instance);
        var pipeline = new SecurityPipeline(
            NullLogger<SecurityPipeline>.Instance,
            configService,
            hitl,
            sessionManager,
            new NoOpHitlPlainLanguage());

        SessionContext.SetSessionId(null);
        var args = new Dictionary<string, object?> { ["scriptCode"] = "return 1;" };
        var result = await pipeline.EvaluateAsync("Browser", "run_custom_javascript_in_page", args, default);
        Assert.NotNull(result);
        Assert.Contains("会话", result, StringComparison.Ordinal);
    }

    private static ConfigService CreateConfigServiceWithCliRunMode(string cliRunMode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var configService = new ConfigService(configuration, NullLogger<ConfigService>.Instance);
        var appConfig = new AppConfig { CliRunMode = cliRunMode };
        var field = typeof(ConfigService).GetField("_currentConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(configService, appConfig);
        return configService;
    }

    private sealed class NoOpHitlPlainLanguage : IHitlPlainLanguageExplainer
    {
        public Task<string?> SummarizeAsync(string rawExecutableText, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("");
    }
}
