using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ClientPageContextSuffixBuilderTests
{
    [Theory]
    [InlineData("WORD", "word")]
    [InlineData("Et", "et")]
    [InlineData("  wpp  ", "wpp")]
    [InlineData("unknown", "unknown")]
    [InlineData("NONE", "none")]
    public void NormalizeWpsHostKind_AcceptsWhitelist(string raw, string expected) =>
        Assert.Equal(expected, ClientPageContextSuffixBuilder.NormalizeWpsHostKind(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("excel")]
    [InlineData("表格")]
    public void NormalizeWpsHostKind_RejectsInvalid(string? raw) =>
        Assert.Null(ClientPageContextSuffixBuilder.NormalizeWpsHostKind(raw));

    [Fact]
    public void Build_WpsEt_IncludesExcelInstruction()
    {
        var s = ClientPageContextSuffixBuilder.Build("wps", "et", "Book1");
        Assert.Contains("电子表格", s);
        Assert.Contains("current_excel_", s);
        Assert.Contains("Book1", s);
        Assert.Contains("仅供参考", s);
    }

    [Fact]
    public void Build_WpsWord_PrefersWordTools()
    {
        var s = ClientPageContextSuffixBuilder.Build("wps", "word", null);
        Assert.Contains("文字处理", s);
        Assert.Contains("current_word_", s);
        Assert.DoesNotContain("仅供参考", s);
    }

    [Fact]
    public void Build_WpsEt_NoPageTitle_OnlyHostBlock()
    {
        var s = ClientPageContextSuffixBuilder.Build("wps", "et", null);
        Assert.Contains("电子表格", s);
        Assert.DoesNotContain("仅供参考", s);
    }

    [Fact]
    public void Build_Chrome_OnlyPageTitle_NoWpsHostBlock()
    {
        var s = ClientPageContextSuffixBuilder.Build("chrome", null, "Example · Tab");
        Assert.Contains("浏览器活动标签", s);
        Assert.Contains("Example · Tab", s);
        Assert.DoesNotContain("WPS", s);
    }

    [Fact]
    public void Build_WpsUnknown_StillGuidesModel()
    {
        var s = ClientPageContextSuffixBuilder.Build("wps", "unknown", "");
        Assert.Contains("未明确上报", s);
    }

    [Fact]
    public void Build_TruncatesLongPageTitle()
    {
        var longTitle = new string('x', ClientPageContextSuffixBuilder.PageTitleInjectMaxChars + 30);
        var s = ClientPageContextSuffixBuilder.Build("wps", "et", longTitle);
        Assert.Contains("…", s);
        Assert.True(s.Length < longTitle.Length + 500);
    }
}
