using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Plan;
using Xunit;

namespace backend.Tests.Unit;

public sealed class PlanAuthoringToolDigestTests
{
    [Fact]
    public void Build_EmptyTools_ReturnsFallback()
    {
        var reg = new ToolRegistry();
        var s = PlanAuthoringToolDigest.Build(Array.Empty<AITool>(), reg);
        Assert.Contains("无可用工具", s);
    }

    [Fact]
    public void Build_IncludesPluginDotNameAndTruncatesDescription()
    {
        var reg = new ToolRegistry();
        var longDesc = new string('z', PlanAuthoringToolDigest.MaxDescriptionCharsPerTool + 50);
        var fn = AIFunctionFactory.Create(
            () => Task.FromResult("ok"),
            new AIFunctionFactoryOptions { Name = "my_func", Description = longDesc });
        reg.Register("Word", "my_func", fn);
        var tools = new List<AITool> { fn };
        var s = PlanAuthoringToolDigest.Build(tools, reg, maxTotalChars: 50_000);
        Assert.Contains("Word.my_func:", s);
        Assert.Contains("…", s);
    }

    [Fact]
    public void Build_OmitsLinesWhenOverMaxTotalChars()
    {
        var reg = new ToolRegistry();
        var list = new List<AITool>();
        for (var i = 0; i < 8; i++)
        {
            var name = "f" + i;
            var fn = AIFunctionFactory.Create(
                () => Task.FromResult("ok"),
                new AIFunctionFactoryOptions { Name = name, Description = "short" });
            reg.Register("X", name, fn);
            list.Add(fn);
        }
        var s = PlanAuthoringToolDigest.Build(list, reg, maxTotalChars: 80);
        Assert.Contains("省略", s);
        Assert.Contains("个工具", s);
    }
}
