using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ToolNeedGateParserTests
{
    [Theory]
    [InlineData("NO", false, true)]
    [InlineData("no", false, true)]
    [InlineData("YES", true, true)]
    [InlineData("yes", true, true)]
    [InlineData("NO。", false, true)]
    [InlineData("**NO**", false, true)]
    public void Parse_LineAnswers(string raw, bool expectBind, bool explicitParse)
    {
        var (bind, ex) = ToolNeedGateParser.Parse(raw);
        Assert.Equal(expectBind, bind);
        Assert.Equal(explicitParse, ex);
    }

    [Fact]
    public void Parse_JsonFalse_BindsFalse()
    {
        var (bind, ex) = ToolNeedGateParser.Parse("""{"needTools":false}""");
        Assert.False(bind);
        Assert.True(ex);
    }

    [Fact]
    public void Parse_JsonTrue_BindsTrue()
    {
        var (bind, ex) = ToolNeedGateParser.Parse("""{"need_tools":true}""");
        Assert.True(bind);
        Assert.True(ex);
    }

    [Fact]
    public void Parse_Empty_ConservativeTrue()
    {
        var (bind, ex) = ToolNeedGateParser.Parse("");
        Assert.True(bind);
        Assert.False(ex);
    }

    [Fact]
    public void Parse_Garbage_ConservativeTrue()
    {
        var (bind, ex) = ToolNeedGateParser.Parse("maybe later");
        Assert.True(bind);
        Assert.False(ex);
    }

    [Fact]
    public void Parse_FirstLineWins()
    {
        var (bind, _) = ToolNeedGateParser.Parse("NO\nextra YES");
        Assert.False(bind);
    }
}
