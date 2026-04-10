using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class ToolCatalogIndexTests
{
    [Fact]
    public void Search_EmptyQuery_ReturnsFirstTopKSortedByFunctionName()
    {
        var reg = new ToolRegistry();
        reg.Register("X", "fn_b", AIFunctionFactory.Create(() => Task.FromResult("1"), new AIFunctionFactoryOptions { Name = "fn_b", Description = "z" }));
        reg.Register("X", "fn_a", AIFunctionFactory.Create(() => Task.FromResult("2"), new AIFunctionFactoryOptions { Name = "fn_a", Description = "y" }));
        var idx = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var hits = idx.Search("", 1);
        Assert.Single(hits);
        Assert.Equal("fn_a", hits[0].FunctionName);
    }

    [Fact]
    public void Search_KeywordPrioritizesMatchingFunctionName()
    {
        var reg = new ToolRegistry();
        reg.Register("X", "excel_read", AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "excel_read", Description = "read cells" }));
        reg.Register("X", "other", AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "other", Description = "excel helper text" }));
        var idx = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var hits = idx.Search("excel", 2);
        Assert.True(hits.Count >= 1);
        Assert.Equal("excel_read", hits[0].FunctionName);
    }

    [Fact]
    public void Search_EmptyQuery_WithPinned_IncludesPinnedEvenWhenNotInAlphabeticalTopK()
    {
        var reg = new ToolRegistry();
        for (var i = 0; i < 40; i++)
        {
            var name = $"z_tool_{i:00}";
            reg.Register("X", name, AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = name, Description = "d" }));
        }

        reg.Register("Browser", "run_page_script", AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "run_page_script", Description = "tabs" }));
        var idx = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var pinned = new[] { "run_page_script" };
        var hits = idx.Search("", 8, pinned);
        Assert.Contains(hits, e => string.Equals(e.FunctionName, "run_page_script", StringComparison.OrdinalIgnoreCase));
    }
}
