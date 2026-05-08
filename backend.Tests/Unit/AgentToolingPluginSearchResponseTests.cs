using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Plugins;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public sealed class AgentToolingPluginSearchResponseTests
{
    private static AgentToolingPlugin CreatePlugin(ToolRegistry reg)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatRuntimeAccessor(sp);
        runtime.SetToolRegistry(reg);
        return new AgentToolingPlugin(runtime, new SessionManager(NullLogger<SessionManager>.Instance), NullLogger<AgentToolingPlugin>.Instance);
    }

    private static DynamicToolingTurnState CreateState(
        ToolRegistry reg,
        SkillCatalogIndex skillCatalog,
        int maxSearch = 12)
    {
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var cfg = new DynamicToolingConfig { MaxSearchPerTurn = maxSearch };
        return new DynamicToolingTurnState(cfg, catalog, skillCatalog);
    }

    private static SkillCatalogIndex SkillCatalogWithOneEntry() =>
        SkillCatalogIndex.BuildFromEnabledSkills(new List<SkillDefinition>
        {
            new() { Id = "s_gate", Enabled = true, Name = "T", Description = "d" }
        });

    [Fact]
    public async Task Search_WithSkillCatalog_AppendsGateReminder()
    {
        var reg = new ToolRegistry();
        reg.Register(
            "Memory",
            "alpha_read",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "alpha_read", Description = "alpha" }));

        var plugin = CreatePlugin(reg);
        var state = CreateState(reg, SkillCatalogWithOneEntry());

        using (DynamicToolingTurnScope.Push(state))
        {
            var msg = await plugin.SearchAvailableToolsAsync("alpha");
            Assert.Contains("[search_available_tools]", msg, StringComparison.Ordinal);
            Assert.Contains("【顺序与门控】", msg, StringComparison.Ordinal);
            Assert.Contains("search_available_skills", msg, StringComparison.Ordinal);
            Assert.Contains("activate_tools", msg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Search_WithEmptySkillCatalog_DoesNotAppendGateReminder()
    {
        var reg = new ToolRegistry();
        reg.Register(
            "Memory",
            "alpha_read",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "alpha_read", Description = "alpha" }));

        var plugin = CreatePlugin(reg);
        var state = CreateState(reg, SkillCatalogIndex.Empty);

        using (DynamicToolingTurnScope.Push(state))
        {
            var msg = await plugin.SearchAvailableToolsAsync("alpha");
            Assert.Contains("[search_available_tools]", msg, StringComparison.Ordinal);
            Assert.DoesNotContain("【顺序与门控】", msg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Search_MaxPerTurn_WithSkills_MentionsSkillSearchBeforeActivate()
    {
        var reg = new ToolRegistry();
        reg.Register(
            "Memory",
            "alpha_read",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "alpha_read", Description = "alpha" }));

        var plugin = CreatePlugin(reg);
        var state = CreateState(reg, SkillCatalogWithOneEntry(), maxSearch: 1);

        using (DynamicToolingTurnScope.Push(state))
        {
            _ = await plugin.SearchAvailableToolsAsync("alpha");
            var msg = await plugin.SearchAvailableToolsAsync("alpha");
            Assert.Contains("已达本轮检索上限", msg, StringComparison.Ordinal);
            Assert.Contains("search_available_skills", msg, StringComparison.Ordinal);
            Assert.Contains("activate_tools", msg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Search_MaxPerTurn_NoSkills_ShorterHint()
    {
        var reg = new ToolRegistry();
        reg.Register(
            "Memory",
            "alpha_read",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "alpha_read", Description = "alpha" }));

        var plugin = CreatePlugin(reg);
        var state = CreateState(reg, SkillCatalogIndex.Empty, maxSearch: 1);

        using (DynamicToolingTurnScope.Push(state))
        {
            _ = await plugin.SearchAvailableToolsAsync("alpha");
            var msg = await plugin.SearchAvailableToolsAsync("alpha");
            Assert.Contains("已达本轮检索上限", msg, StringComparison.Ordinal);
            Assert.Contains("activate_tools", msg, StringComparison.Ordinal);
            Assert.DoesNotContain("search_available_skills", msg, StringComparison.Ordinal);
        }
    }
}
