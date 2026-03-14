using OfficeCopilot.Server.Services.Memory;
using Xunit;

namespace backend.Tests.Unit;

public class TextChunkerTests
{
    [Fact]
    public void Chunk_EmptyString_ReturnsEmpty()
    {
        var result = TextChunker.Chunk("");
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_WhitespaceOnly_ReturnsEmpty()
    {
        var result = TextChunker.Chunk("   \n\n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_Null_ReturnsEmpty()
    {
        var result = TextChunker.Chunk(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_ShortText_ReturnsSingleChunk()
    {
        var text = "Short single paragraph.";
        var result = TextChunker.Chunk(text, maxChunkChars: 800);
        Assert.Single(result);
        Assert.Equal(text, result[0]);
    }

    [Fact]
    public void Chunk_TextWithinMaxChars_ReturnsSingleChunk()
    {
        var text = "Hello world within limit.";
        var result = TextChunker.Chunk(text, maxChunkChars: 200);
        Assert.Single(result);
        Assert.Equal(text, result[0]);
    }

    [Fact]
    public void Chunk_LongText_SplitsIntoMultipleChunks()
    {
        var p1 = new string('a', 400);
        var p2 = new string('b', 400);
        var text = p1 + "\n\n" + p2;
        var result = TextChunker.Chunk(text, maxChunkChars: 500);
        Assert.True(result.Count >= 2);
        Assert.True(result.All(c => c.Length <= 500 + 100)); // allow some overlap
    }

    [Fact]
    public void Chunk_RespectsParagraphBoundaries()
    {
        var para1 = "First paragraph.";
        var para2 = "Second paragraph.";
        var para3 = "Third paragraph.";
        var text = para1 + "\n\n" + para2 + "\n\n" + para3;
        var result = TextChunker.Chunk(text, maxChunkChars: 1000);
        Assert.Single(result);
        Assert.Contains(para1, result[0]);
        Assert.Contains(para2, result[0]);
        Assert.Contains(para3, result[0]);
    }

    [Fact]
    public void Chunk_Overlap_IncludesOverlapInNextChunk()
    {
        // Use multiple paragraphs so chunker splits; single long paragraph returns one chunk
        var p1 = new string('a', 400);
        var p2 = new string('b', 400);
        var p3 = new string('c', 100);
        var text = p1 + "\n\n" + p2 + "\n\n" + p3;
        var result = TextChunker.Chunk(text, maxChunkChars: 400, overlapChars: 50);
        Assert.True(result.Count >= 2);
        Assert.True(result[0].Length <= 450);
        if (result.Count >= 2 && result[0].Length >= 50)
        {
            var overlap = result[0].AsSpan(result[0].Length - 50);
            Assert.True(result[1].AsSpan().StartsWith(overlap));
        }
    }

    [Fact]
    public void Chunk_TrimsInput()
    {
        var text = "  hello  ";
        var result = TextChunker.Chunk(text, maxChunkChars: 100);
        Assert.Single(result);
        Assert.Equal("hello", result[0]);
    }

    [Fact]
    public void Chunk_CustomMaxAndOverlap_RespectsParameters()
    {
        var text = new string('a', 200) + "\n\n" + new string('b', 200);
        var result = TextChunker.Chunk(text, maxChunkChars: 150, overlapChars: 20);
        Assert.True(result.Count >= 2);
    }
}
