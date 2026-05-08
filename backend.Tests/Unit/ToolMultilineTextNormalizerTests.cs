using OpenWorkmate.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class ToolMultilineTextNormalizerTests
{
    [Fact]
    public void Normalize_PipeSegments_JoinedWithNewline()
    {
        var s = ToolMultilineTextNormalizer.NormalizeToNewlineSeparatedLines("a|b|c");
        Assert.Equal("a\nb\nc", s);
    }

    [Fact]
    public void Normalize_EmptyAndWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", ToolMultilineTextNormalizer.NormalizeToNewlineSeparatedLines(null));
        Assert.Equal("", ToolMultilineTextNormalizer.NormalizeToNewlineSeparatedLines(""));
        Assert.Equal("", ToolMultilineTextNormalizer.NormalizeToNewlineSeparatedLines("   "));
    }

    [Fact]
    public void Normalize_DoubleNewline_SplitsLikeWord()
    {
        var s = ToolMultilineTextNormalizer.NormalizeToNewlineSeparatedLines("第一段\n\n第二段");
        Assert.Equal("第一段\n第二段", s);
    }
}
