using OpenWorkmate.Server.Logging;
using Xunit;

namespace backend.Tests.Unit;

public class LogPreviewReasoningStreamLogTests
{
    [Fact]
    public void AppendChunk_Empty_DoesNothing()
    {
        var s = new LogPreview.ReasoningStreamLogState();
        s.AppendChunk("");
        s.AppendChunk(null);
        Assert.Equal(0, s.TotalChars);
        Assert.Equal("", s.BuildPreview());
    }

    [Fact]
    public void BuildPreview_ShortSingleChunk_FullText()
    {
        var s = new LogPreview.ReasoningStreamLogState();
        s.AppendChunk("hello");
        Assert.Equal(5, s.TotalChars);
        Assert.Equal("hello", s.BuildPreview());
    }

    [Fact]
    public void BuildPreview_Exactly192_StitchedWithoutEllipsis()
    {
        var s = new LogPreview.ReasoningStreamLogState();
        var body = new string('a', 192);
        s.AppendChunk(body);
        Assert.Equal(192, s.TotalChars);
        var p = s.BuildPreview();
        Assert.DoesNotContain('…', p);
        Assert.Equal(192, p.Length);
    }

    [Fact]
    public void BuildPreview_Over192_HeadEllipsisTail()
    {
        var s = new LogPreview.ReasoningStreamLogState();
        var body = new string('x', 96) + new string('y', 97);
        s.AppendChunk(body);
        Assert.Equal(193, s.TotalChars);
        var p = s.BuildPreview();
        Assert.Contains('…', p);
        Assert.StartsWith(new string('x', 96), p);
        Assert.EndsWith(new string('y', 96), p);
    }

    [Fact]
    public void AppendChunk_MultiChunk_StitchesAt100Chars()
    {
        var s = new LogPreview.ReasoningStreamLogState();
        s.AppendChunk(new string('a', 50));
        s.AppendChunk(new string('b', 50));
        Assert.Equal(100, s.TotalChars);
        var p = s.BuildPreview();
        Assert.Equal(100, p.Length);
        Assert.StartsWith(new string('a', 50), p);
        Assert.EndsWith(new string('b', 50), p);
    }

    [Fact]
    public void BuildPreview_Newlines_SingleLined()
    {
        var s = new LogPreview.ReasoningStreamLogState();
        s.AppendChunk("a\nb");
        Assert.Equal("a b", s.BuildPreview());
    }

    /// <summary>与 Program.HandleChatStreamAsync 中 async 局部函数捕获 struct 的行为一致。</summary>
    [Fact]
    public async System.Threading.Tasks.Task AppendChunk_AsyncClosure_PersistsAcrossYield()
    {
        var s = new LogPreview.ReasoningStreamLogState();
        async System.Threading.Tasks.Task AppendTwice()
        {
            await System.Threading.Tasks.Task.Yield();
            s.AppendChunk("a");
            await System.Threading.Tasks.Task.Yield();
            s.AppendChunk("bc");
        }

        await AppendTwice();
        Assert.Equal(3, s.TotalChars);
        Assert.Equal("abc", s.BuildPreview());
    }
}
