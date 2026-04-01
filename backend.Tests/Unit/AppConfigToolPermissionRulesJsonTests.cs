using System.Text.Json;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Unit;

public class AppConfigToolPermissionRulesJsonTests
{
    [Fact]
    public void Deserialize_CamelCase_ToolPermissionRules()
    {
        const string json = """{"toolPermissionRules":[{"pattern":"CLI:*","effect":"deny"}]}""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, ConfigService.AppConfigDeserializeOptions);
        Assert.NotNull(cfg?.ToolPermissionRules);
        Assert.Single(cfg.ToolPermissionRules);
        Assert.Equal("CLI:*", cfg.ToolPermissionRules[0].Pattern);
        Assert.Equal("deny", cfg.ToolPermissionRules[0].Effect);
    }

    [Fact]
    public void Deserialize_ContextWindow_SessionAudit_CamelCase()
    {
        const string json = """{"contextWindow":{"sessionAuditEnabled":true,"sessionAuditDirectory":"D:/audit"}}""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, ConfigService.AppConfigDeserializeOptions);
        Assert.NotNull(cfg?.ContextWindow);
        Assert.True(cfg.ContextWindow.SessionAuditEnabled);
        Assert.Equal("D:/audit", cfg.ContextWindow.SessionAuditDirectory);
    }
}
