using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class SessionToolResolverDynamicBootstrapTests
{
    [Fact]
    public void GetDynamicBootstrapTools_Chrome_IncludesRunPageScriptAndRunCustomPageScript()
    {
        var reg = new ToolRegistry();
        void Reg(string func) =>
            reg.Register("Browser", func, AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = func, Description = "d" }));
        Reg("run_page_script");
        Reg("run_custom_page_script");
        reg.Register("AgentTooling", DynamicToolingConstants.SearchFunctionName,
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = DynamicToolingConstants.SearchFunctionName, Description = "" }));
        reg.Register("AgentTooling", DynamicToolingConstants.ActivateFunctionName,
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = DynamicToolingConstants.ActivateFunctionName, Description = "" }));
        reg.Register("CLI", "run_command",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "run_command", Description = "" }));

        var tools = SessionToolResolver.GetDynamicBootstrapTools(reg, "chrome", null, mergePlanTools: false);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("run_page_script", names);
        Assert.Contains("run_custom_page_script", names);
        Assert.Contains(DynamicToolingConstants.SearchFunctionName, names);
        Assert.Contains(DynamicToolingConstants.ActivateFunctionName, names);
        Assert.Contains("run_command", names);
    }

    [Fact]
    public void BuildDynamicActiveToolList_MergesActivatedOntoBootstrap()
    {
        var reg = new ToolRegistry();
        reg.Register("AgentTooling", DynamicToolingConstants.SearchFunctionName,
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = DynamicToolingConstants.SearchFunctionName, Description = "" }));
        reg.Register("AgentTooling", DynamicToolingConstants.ActivateFunctionName,
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = DynamicToolingConstants.ActivateFunctionName, Description = "" }));
        reg.Register("Browser", "run_page_script",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "run_page_script", Description = "" }));
        reg.Register("Browser", "run_custom_page_script",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "run_custom_page_script", Description = "" }));
        reg.Register("CLI", "run_command",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "run_command", Description = "" }));
        reg.Register("Word", "word_document_create",
            AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "word_document_create", Description = "" }));

        var cfg = new DynamicToolingConfig { Enabled = true };
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var state = new DynamicToolingTurnState(cfg, catalog);
        state.ActivatedFunctionNames.Add("word_document_create");

        var list = SessionToolResolver.BuildDynamicActiveToolList(reg, state, "chrome", null, mergePlanTools: false);
        Assert.Contains(list, t => string.Equals(t.Name, "word_document_create", StringComparison.OrdinalIgnoreCase));
    }
}
