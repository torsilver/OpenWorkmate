using OpenWorkmate.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class WordDocumentCreateHarnessTests
{
    [Fact]
    public void TryValidate_NullOrEmpty_Passes()
    {
        Assert.True(WordDocumentCreateHarness.TryValidateParsedParagraphs(null, out var m1));
        Assert.Null(m1);
        Assert.True(WordDocumentCreateHarness.TryValidateParsedParagraphs(Array.Empty<string>(), out var m2));
        Assert.Null(m2);
    }

    [Fact]
    public void TryValidate_OnlyWhitespace_Passes()
    {
        Assert.True(WordDocumentCreateHarness.TryValidateParsedParagraphs(new[] { "  ", "\n" }, out var m));
        Assert.Null(m);
    }

    [Fact]
    public void TryValidate_NormalMarkdown_Passes()
    {
        var ok = WordDocumentCreateHarness.TryValidateParsedParagraphs(
            new[] { "正常段落。", "## 小节\n正文一段。" },
            out var m);
        Assert.True(ok);
        Assert.Null(m);
    }

    [Fact]
    public void TryValidate_ShortBracketPrefix_Passes()
    {
        // 长度不足，不进入「疑似 JSON 数组」分支
        Assert.True(WordDocumentCreateHarness.TryValidateParsedParagraphs(new[] { "[1] 短" }, out var m));
        Assert.Null(m);
    }

    [Fact]
    public void TryValidate_InvalidJsonWithCommaDelimiter_Rejected()
    {
        var bad = """["abcdefghijkl","mnopqrstuv"""; // 缺少结尾，非法 JSON
        Assert.False(WordDocumentCreateHarness.TryValidateParsedParagraphs(new[] { "前言", bad }, out var m));
        Assert.NotNull(m);
        Assert.StartsWith("[无效]", m, StringComparison.Ordinal);
        Assert.Contains("第 2 项", m, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidate_ArrayWithNonStringElement_Rejected()
    {
        var bad = """["abcdefghijkl",1]""";
        Assert.False(WordDocumentCreateHarness.TryValidateParsedParagraphs(new[] { bad }, out var m));
        Assert.NotNull(m);
        Assert.StartsWith("[无效]", m, StringComparison.Ordinal);
        Assert.Contains("非字符串", m, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidate_LongBracketLiteralUnparseable_Rejected()
    {
        var bad = "[" + new string('x', 78) + "]";
        Assert.True(bad.Length >= 80);
        Assert.False(WordDocumentCreateHarness.TryValidateParsedParagraphs(new[] { bad }, out var m));
        Assert.NotNull(m);
        Assert.StartsWith("[无效]", m, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidate_ValidJsonStringArrayOneSegment_Passes()
    {
        // 计划：解析为全字符串数组则放行（解析器通常已展开；此处覆盖 harness 不误杀合法 JSON 文本边界的约定）
        var inner = """["# A","## B"]""";
        Assert.True(WordDocumentCreateHarness.TryValidateParsedParagraphs(new[] { inner }, out var m));
        Assert.Null(m);
    }
}
