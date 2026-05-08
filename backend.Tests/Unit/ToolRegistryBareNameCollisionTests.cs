using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ToolRegistryBareNameCollisionTests
{
    [Fact]
    public void GetBareFunctionNameCollisions_ReturnsDuplicatesAcrossPlugins()
    {
        var reg = new ToolRegistry();
        reg.Register(
            "PluginA",
            "same_name",
            AIFunctionFactory.Create(
                static () => "a",
                new AIFunctionFactoryOptions { Name = "same_name", Description = "A" }));
        reg.Register(
            "PluginB",
            "same_name",
            AIFunctionFactory.Create(
                static () => "b",
                new AIFunctionFactoryOptions { Name = "same_name", Description = "B" }));

        var collisions = reg.GetBareFunctionNameCollisions();
        var row = Assert.Single(collisions);
        Assert.Equal("same_name", row.FunctionName);
        Assert.Equal(2, row.PluginNames.Count);
        Assert.Contains("PluginA", row.PluginNames);
        Assert.Contains("PluginB", row.PluginNames);
    }

    [Fact]
    public void GetBareFunctionNameCollisions_EmptyWhenUnique()
    {
        var reg = new ToolRegistry();
        reg.Register(
            "Word",
            "word_body_read",
            AIFunctionFactory.Create(static () => "x", new AIFunctionFactoryOptions { Name = "word_body_read", Description = "r" }));
        Assert.Empty(reg.GetBareFunctionNameCollisions());
    }
}
