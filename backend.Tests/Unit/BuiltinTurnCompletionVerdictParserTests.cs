using OfficeCopilot.Server.Services;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class BuiltinTurnCompletionVerdictParserTests
{
    [Fact]
    public void ParseModelOutput_complete_true_ok()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput("""{"complete":true,"reason":"已答复"}""");
        Assert.True(r.ParseOk);
        Assert.False(r.Incomplete);
        Assert.Equal("已答复", r.Reason);
    }

    [Fact]
    public void ParseModelOutput_complete_false_triggers_incomplete()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput("""{"complete":false,"reason":"缺正文"}""");
        Assert.True(r.ParseOk);
        Assert.True(r.Incomplete);
        Assert.Equal("缺正文", r.Reason);
    }

    [Fact]
    public void ParseModelOutput_fenced_json_ok()
    {
        var raw = """
            ```json
            {"complete":false,"reason":"x"}
            ```
            """;
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput(raw);
        Assert.True(r.ParseOk);
        Assert.True(r.Incomplete);
    }

    [Fact]
    public void ParseModelOutput_extra_prose_still_finds_object()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput("here: {\"complete\":true}");
        Assert.True(r.ParseOk);
        Assert.False(r.Incomplete);
    }

    [Fact]
    public void ParseModelOutput_missing_complete_not_ok()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput("""{"reason":"only"}""");
        Assert.False(r.ParseOk);
        Assert.False(r.Incomplete);
    }

    [Fact]
    public void ParseModelOutput_invalid_json_not_ok()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput("not json");
        Assert.False(r.ParseOk);
    }

    [Fact]
    public void ParseModelOutput_complete_string_true_ok()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput("""{"complete":"true","reason":""}""");
        Assert.True(r.ParseOk);
        Assert.False(r.Incomplete);
    }
}
