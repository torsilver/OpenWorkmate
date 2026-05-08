using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class PdfToolNormalizeTests
{
    [Theory]
    [InlineData(null, PdfToolNormalize.DefaultMaxChars)]
    [InlineData(0, PdfToolNormalize.DefaultMaxChars)]
    [InlineData(-1, PdfToolNormalize.DefaultMaxChars)]
    [InlineData(100, 100)]
    [InlineData(PdfToolNormalize.AbsoluteMaxChars + 1, PdfToolNormalize.AbsoluteMaxChars)]
    public void NormalizeMaxChars_ReturnsExpected(int? input, int expected)
    {
        Assert.Equal(expected, PdfToolNormalize.NormalizeMaxChars(input));
    }

    [Fact]
    public void TryNormalizePageRange_PageCountInvalid_ReturnsFalse()
    {
        var ok = PdfToolNormalize.TryNormalizePageRange(0, 1, 1, out _, out _, out var err);
        Assert.False(ok);
        Assert.Contains("页数", err ?? "");
    }

    [Fact]
    public void TryNormalizePageRange_DefaultsToFullDocument()
    {
        Assert.True(PdfToolNormalize.TryNormalizePageRange(10, null, null, out var from, out var to, out _));
        Assert.Equal(1, from);
        Assert.Equal(10, to);
    }

    [Fact]
    public void TryNormalizePageRange_ClampsAndSwaps()
    {
        Assert.True(PdfToolNormalize.TryNormalizePageRange(5, 10, -3, out var from, out var to, out _));
        Assert.Equal(1, from);
        Assert.Equal(5, to);

        Assert.True(PdfToolNormalize.TryNormalizePageRange(5, 4, 2, out from, out to, out _));
        Assert.Equal(2, from);
        Assert.Equal(4, to);
    }
}
