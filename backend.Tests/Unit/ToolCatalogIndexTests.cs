using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class ToolCatalogIndexTests
{
    [Fact]
    public void Search_AppliesFunctionSuccessBoost_WhenProvided()
    {
        var registry = new ToolRegistry();
        registry.Register("Memory", "alpha_read", AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "alpha_read", Description = "alpha" }));
        registry.Register("Memory", "beta_read", AIFunctionFactory.Create(() => Task.FromResult(""), new AIFunctionFactoryOptions { Name = "beta_read", Description = "beta" }));
        var index = ToolCatalogIndex.BuildFromAllowedTools(registry, "chrome", sessionId: "s1", wpsHostKind: null);

        var boost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["beta_read"] = 50 };
        var hits = index.Search("read", topK: 2, pinnedFunctionNames: null, functionSuccessBoost: boost);

        Assert.Equal(2, hits.Count);
        Assert.Equal("beta_read", hits[0].FunctionName);
    }
}
