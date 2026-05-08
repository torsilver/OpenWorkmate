using OpenWorkmate.Server.Services;
using Xunit;

namespace OpenWorkmate.Server.Tests.Unit;

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
        Assert.Equal(TurnCompletionVerifierOutcome.Done, r.Outcome);
    }

    [Fact]
    public void ParseModelOutput_outcome_ask_user()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput(
            """{"outcome":"ask_user","reason":"意图不明"}""");
        Assert.True(r.ParseOk);
        Assert.Equal(TurnCompletionVerifierOutcome.AskUser, r.Outcome);
        Assert.False(r.Incomplete);
        Assert.Equal("意图不明", r.Reason);
    }

    [Fact]
    public void ParseModelOutput_outcome_need_more_work()
    {
        var r = BuiltinTurnCompletionVerdictParser.ParseModelOutput(
            """{"outcome":"need_more_work","reason":"缺工具结果"}""");
        Assert.True(r.ParseOk);
        Assert.Equal(TurnCompletionVerifierOutcome.NeedMoreWork, r.Outcome);
        Assert.True(r.Incomplete);
    }
}

