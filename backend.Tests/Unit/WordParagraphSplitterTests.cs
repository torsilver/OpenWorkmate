using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class WordParagraphSplitterTests
{
    [Fact]
    public void ExpandWordDocumentParagraphs_PipeOnly_YieldsTwoLines()
    {
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs("第一段|第二段").ToList();
        Assert.Equal(new[] { "第一段", "第二段" }, lines);
    }

    [Fact]
    public void ExpandWordDocumentParagraphs_NoPipe_DoubleNewline_SplitsParagraphs()
    {
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs("第一段\n\n第二段").ToList();
        Assert.Equal(new[] { "第一段", "第二段" }, lines);
    }

    [Fact]
    public void ExpandWordDocumentParagraphs_NoPipe_SingleNewline_SplitsLines()
    {
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs("a\nb\nc").ToList();
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void ExpandWordDocumentParagraphs_MixedPipeAndBlankLine()
    {
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs("A|B\n\nC").ToList();
        Assert.Equal(new[] { "A", "B", "C" }, lines);
    }

    [Fact]
    public void ExpandWordDocumentParagraphs_EmptyPipeSegments_Skipped()
    {
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs("x||y").ToList();
        Assert.Equal(new[] { "x", "y" }, lines);
    }

    [Fact]
    public void ExpandWordDocumentParagraphs_EmptyInput_YieldsNothing()
    {
        Assert.Empty(WordParagraphSplitter.ExpandWordDocumentParagraphs(""));
    }

    [Fact]
    public void ExpandWordDocumentParagraphs_CrlfAndWhitespaceBetweenBlankLines()
    {
        var lines = WordParagraphSplitter.ExpandWordDocumentParagraphs("p1\r\n \r\np2").ToList();
        Assert.Equal(new[] { "p1", "p2" }, lines);
    }

    [Fact]
    public void ExpandPipeSegment_TrimsOuterWhitespace()
    {
        var lines = WordParagraphSplitter.ExpandPipeSegment("  hello  \n\n  world  ").ToList();
        Assert.Equal(new[] { "hello", "world" }, lines);
    }

    [Fact]
    public void ExpandPipeSegment_OnlyWhitespace_YieldsNothing()
    {
        Assert.Empty(WordParagraphSplitter.ExpandPipeSegment("   \n\n   "));
    }
}
