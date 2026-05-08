using OpenWorkmate.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class WordParagraphSplitterTests
{
    [Fact]
    public void ExpandWordDocumentParagraphs_PipeSplits()
    {
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs("a|b|c").ToList();
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void ExpandPipeSegment_DoubleNewlineSplitsBlocks()
    {
        var lines = WordParagraphSplitter.ExpandPipeSegment("p1\n\np2").ToList();
        Assert.Equal(new[] { "p1", "p2" }, lines);
    }

    [Fact]
    public void ExpandPipeSegment_SingleBlockMultiline_YieldsLines()
    {
        var lines = WordParagraphSplitter.ExpandPipeSegment("x\ny").ToList();
        Assert.Equal(new[] { "x", "y" }, lines);
    }
}
