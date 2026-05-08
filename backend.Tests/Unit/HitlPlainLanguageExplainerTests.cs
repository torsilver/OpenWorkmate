using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class HitlPlainLanguageExplainerTests
{
    [Fact]
    public void TruncateRawExecutable_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", HitlPlainLanguageExplainer.TruncateRawExecutable(null));
        Assert.Equal("", HitlPlainLanguageExplainer.TruncateRawExecutable(""));
    }

    [Fact]
    public void TruncateRawExecutable_UnderLimit_Unchanged()
    {
        const string s = "dir /w";
        Assert.Same(s, HitlPlainLanguageExplainer.TruncateRawExecutable(s, 100));
    }

    [Fact]
    public void TruncateRawExecutable_OverLimit_AppendsMarker()
    {
        var raw = new string('a', 20);
        var outStr = HitlPlainLanguageExplainer.TruncateRawExecutable(raw, 10);
        Assert.Equal(10 + "\n\n[...已截断]".Length, outStr.Length);
        Assert.StartsWith("aaaaaaaaaa", outStr);
        Assert.EndsWith("\n\n[...已截断]", outStr);
    }

    [Fact]
    public void TruncateRawExecutable_ScriptIdOnly_PassesThrough()
    {
        Assert.Equal("word_read_selection", HitlPlainLanguageExplainer.TruncateRawExecutable("word_read_selection"));
    }

    [Fact]
    public void StripReasoningTags_RemovesThinkingBlocks()
    {
        var raw = "前<thinking>内</thinking>后";
        var cleaned = HitlPlainLanguageExplainer.StripReasoningTags(raw);
        Assert.Equal("前后", cleaned);
    }

    [Fact]
    public void StripReasoningTags_ThinkPair_CaseInsensitive()
    {
        var raw = "a<THINK>x</think>b";
        var cleaned = HitlPlainLanguageExplainer.StripReasoningTags(raw);
        Assert.Equal("ab", cleaned);
    }
}
