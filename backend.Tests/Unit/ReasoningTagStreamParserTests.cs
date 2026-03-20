using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ReasoningTagStreamParserTests
{
    [Fact]
    public void Append_Empty_ReturnsEmpty()
    {
        var p = new ReasoningTagStreamParser();
        Assert.Empty(p.Append(""));
        Assert.Empty(p.Append(null!));
    }

    [Fact]
    public void OnlyAnswer_NoTags_AllAnswer()
    {
        var p = new ReasoningTagStreamParser();
        var a = p.Append("Hello world");
        Assert.Single(a);
        Assert.False(a[0].IsReasoning);
        Assert.Equal("Hello world", a[0].Text);
        var f = p.Flush();
        Assert.Empty(f);
    }

    [Fact]
    public void ThinkingBlock_SplitsAnswer()
    {
        var p = new ReasoningTagStreamParser();
        var a = p.Append("<thinking>step1</thinking>Hi");
        Assert.Equal(2, a.Count);
        Assert.True(a[0].IsReasoning);
        Assert.Equal("step1", a[0].Text);
        Assert.False(a[1].IsReasoning);
        Assert.Equal("Hi", a[1].Text);
    }

    [Fact]
    public void ChunkSplitsOpenTagAcrossCalls()
    {
        var p = new ReasoningTagStreamParser();
        var a1 = p.Append("\u003cthink");
        Assert.Empty(a1);
        var a2 = p.Append(">in\u003c/think\u003eout");
        Assert.Equal(2, a2.Count);
        Assert.True(a2[0].IsReasoning);
        Assert.Equal("in", a2[0].Text);
        Assert.False(a2[1].IsReasoning);
        Assert.Equal("out", a2[1].Text);
    }

    [Fact]
    public void UnclosedReasoning_FlushGoesToReasoning()
    {
        var p = new ReasoningTagStreamParser();
        p.Append("<thinking>no close");
        var f = p.Flush();
        Assert.Single(f);
        Assert.True(f[0].IsReasoning);
        Assert.Equal("no close", f[0].Text);
    }

    [Fact]
    public void StripReasoningTags_RemovesBlocks()
    {
        var raw = "<thinking>a</thinking>User visible<thought>b</thought>tail";
        var s = ReasoningTagStreamParser.StripReasoningTags(raw);
        Assert.Equal("User visibletail", s);
    }

    [Fact]
    public void Reasoning_ThenAnswer_ThenReasoning()
    {
        var p = new ReasoningTagStreamParser();
        var parts = new List<(bool, string)>();
        foreach (var x in p.Append("<reasoning>r1</reasoning>A"))
            parts.Add(x);
        foreach (var x in p.Append("<reasoning>r2</reasoning>B"))
            parts.Add(x);
        parts.AddRange(p.Flush());
        Assert.Equal(4, parts.Count);
        Assert.True(parts[0].Item1);
        Assert.Equal("r1", parts[0].Item2);
        Assert.False(parts[1].Item1);
        Assert.Equal("A", parts[1].Item2);
        Assert.True(parts[2].Item1);
        Assert.Equal("r2", parts[2].Item2);
        Assert.False(parts[3].Item1);
        Assert.Equal("B", parts[3].Item2);
    }
}
