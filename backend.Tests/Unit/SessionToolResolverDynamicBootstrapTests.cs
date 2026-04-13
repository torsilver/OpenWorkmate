using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class SessionToolResolverDynamicBootstrapTests
{
    private static void RegisterChromeBootstrapShell(ToolRegistry reg)
    {
        void RegBrowser(string func) =>
            reg.Register("Browser", func, AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = func, Description = "d" }));
        RegBrowser("run_page_script");
        RegBrowser("run_custom_page_script");
        reg.Register("AgentTooling", DynamicToolingConstants.SearchFunctionName,
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = DynamicToolingConstants.SearchFunctionName, Description = "" }));
        reg.Register("AgentTooling", DynamicToolingConstants.ActivateFunctionName,
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = DynamicToolingConstants.ActivateFunctionName, Description = "" }));
        reg.Register("CLI", "run_command",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "run_command", Description = "" }));
        reg.Register("UserSkillProgressive", DynamicToolingConstants.LoadUserSkillInstructionsFunctionName,
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = DynamicToolingConstants.LoadUserSkillInstructionsFunctionName, Description = "" }));
    }

    [Fact]
    public void GetDynamicBootstrapTools_Chrome_IncludesRunPageScriptAndRunCustomPageScriptAndLoadSkill()
    {
        var reg = new ToolRegistry();
        RegisterChromeBootstrapShell(reg);

        var tools = SessionToolResolver.GetDynamicBootstrapTools(reg, "chrome", null, mergePlanTools: false);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("run_page_script", names);
        Assert.Contains("run_custom_page_script", names);
        Assert.Contains(DynamicToolingConstants.SearchFunctionName, names);
        Assert.Contains(DynamicToolingConstants.ActivateFunctionName, names);
        Assert.Contains("run_command", names);
        Assert.Contains(DynamicToolingConstants.LoadUserSkillInstructionsFunctionName, names);
    }

    [Fact]
    public void GetDynamicBootstrapTools_LegacyBootstrapUserSkillIds_DoesNotAddPerSkillTools()
    {
        var reg = new ToolRegistry();
        RegisterChromeBootstrapShell(reg);

        var cfg = new DynamicToolingConfig { BootstrapUserSkillIds = new List<string> { "MySkill" } };
        var skills = new List<SkillDefinition>
        {
            new() { Id = "MySkill", Enabled = true, PromptTemplate = "x" }
        };

        var tools = SessionToolResolver.GetDynamicBootstrapTools(
            reg, "chrome", null, mergePlanTools: false, cfg, skills, NullLogger.Instance);
        Assert.DoesNotContain(tools, t => string.Equals(t.Name, "MySkill", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, t => string.Equals(t.Name, DynamicToolingConstants.LoadUserSkillInstructionsFunctionName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetDynamicBootstrapTools_IncludeAllEnabledUserSkills_DoesNotAddPerSkillTools()
    {
        var reg = new ToolRegistry();
        RegisterChromeBootstrapShell(reg);

        var cfg = new DynamicToolingConfig { BootstrapIncludeAllEnabledUserSkills = true };
        var skills = new List<SkillDefinition>
        {
            new() { Id = "A", Enabled = true, PromptTemplate = "p" },
            new() { Id = "B", Enabled = true, PromptTemplate = "p" }
        };

        var tools = SessionToolResolver.GetDynamicBootstrapTools(
            reg, "chrome", null, mergePlanTools: false, cfg, skills, NullLogger.Instance);
        Assert.DoesNotContain(tools, t => string.Equals(t.Name, "A", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tools, t => string.Equals(t.Name, "B", StringComparison.OrdinalIgnoreCase));
        Assert.Single(tools, t => string.Equals(t.Name, DynamicToolingConstants.LoadUserSkillInstructionsFunctionName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDynamicActiveToolList_WithOrderedBootstrap_MergesActivatedOntoBootstrap()
    {
        var reg = new ToolRegistry();
        RegisterChromeBootstrapShell(reg);
        reg.Register("Word", "word_document_create",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "word_document_create", Description = "" }));

        var cfg = new DynamicToolingConfig { Enabled = true };
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var bootstrap = SessionToolResolver.GetDynamicBootstrapTools(reg, "chrome", null, mergePlanTools: false);
        var orderedNames = bootstrap.Select(t => t.Name!).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var state = new DynamicToolingTurnState(cfg, catalog, orderedNames);
        state.ActivatedFunctionNames.Add("word_document_create");

        var list = SessionToolResolver.BuildDynamicActiveToolList(reg, state, "chrome", null, mergePlanTools: false);
        Assert.Contains(list, t => string.Equals(t.Name, "word_document_create", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, t => string.Equals(t.Name, DynamicToolingConstants.SearchFunctionName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, t => string.Equals(t.Name, DynamicToolingConstants.LoadUserSkillInstructionsFunctionName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MaterializeBootstrapFromOrderedFunctionNames_RespectsClientFilter()
    {
        var reg = new ToolRegistry();
        RegisterChromeBootstrapShell(reg);
        reg.Register("CurrentDocument", "current_word_read_body",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "current_word_read_body", Description = "" }));

        var ordered = new List<string> { DynamicToolingConstants.SearchFunctionName, "current_word_read_body" };
        var tools = SessionToolResolver.MaterializeBootstrapFromOrderedFunctionNames(reg, ordered, "chrome", null, mergePlanTools: false);
        Assert.Contains(tools, t => string.Equals(t.Name, DynamicToolingConstants.SearchFunctionName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tools, t => string.Equals(t.Name, "current_word_read_body", StringComparison.OrdinalIgnoreCase));
    }
}
