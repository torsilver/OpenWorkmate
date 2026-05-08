using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class TextFileToolNormalizeTests
{
    [Theory]
    [InlineData(null, PdfToolNormalize.DefaultMaxChars)]
    [InlineData(0, PdfToolNormalize.DefaultMaxChars)]
    [InlineData(500, 500)]
    [InlineData(PdfToolNormalize.AbsoluteMaxChars + 10, PdfToolNormalize.AbsoluteMaxChars)]
    public void NormalizeMaxChars_MatchesPdfPolicy(int? input, int expected)
    {
        Assert.Equal(expected, TextFileToolNormalize.NormalizeMaxChars(input));
    }

    [Fact]
    public void ApplyMaxCharLimit_UnderLimit_Unchanged()
    {
        var s = TextFileToolNormalize.ApplyMaxCharLimit("hello", 10, out var truncated);
        Assert.False(truncated);
        Assert.Equal("hello", s);
    }

    [Fact]
    public void ApplyMaxCharLimit_AtLimit_Unchanged()
    {
        var s = TextFileToolNormalize.ApplyMaxCharLimit("abcde", 5, out var truncated);
        Assert.False(truncated);
        Assert.Equal("abcde", s);
    }

    [Fact]
    public void ApplyMaxCharLimit_OverLimit_TruncatesWithSuffix()
    {
        var s = TextFileToolNormalize.ApplyMaxCharLimit("abcdef", 3, out var truncated);
        Assert.True(truncated);
        Assert.Equal("abc" + TextFileToolNormalize.TruncationSuffix, s);
    }

    [Fact]
    public void ApplyMaxCharLimit_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TextFileToolNormalize.ApplyMaxCharLimit(null!, 5, out _));
    }
}
