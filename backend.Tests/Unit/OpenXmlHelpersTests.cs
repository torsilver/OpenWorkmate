using System.Runtime.InteropServices;
using OpenWorkmate.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class OpenXmlHelpersTests
{
    [Fact]
    public void ResolvePath_OnWindows_RemapsPublicDownloadsToUserDownloads()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(publicRoot) || string.IsNullOrEmpty(userProfile)) return;

        var input = Path.Combine(publicRoot, "Downloads", "OpenXmlRemapTest.docx");
        var expected = Path.Combine(userProfile, "Downloads", "OpenXmlRemapTest.docx");
        var actual = OpenXmlHelpers.ResolvePath(input);
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual));
    }

    [Fact]
    public void ResolvePath_OnWindows_RemapsPercentPublicDownloads()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return;

        var actual = OpenXmlHelpers.ResolvePath(Path.Combine("%PUBLIC%", "Downloads", "x.docx"));
        var expected = Path.Combine(userProfile, "Downloads", "x.docx");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual));
    }

    [Fact]
    public void ResolvePath_RelativeStillGoesToUserDownloads()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return;

        var actual = OpenXmlHelpers.ResolvePath("only-name.docx");
        var expected = Path.Combine(userProfile, "Downloads", "only-name.docx");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual));
    }

    [Theory]
    [InlineData("report.md", "report.docx")]
    [InlineData("a.MARKDOWN", "a.docx")]
    [InlineData("note.txt", "note.docx")]
    [InlineData("legacy.doc", "legacy.docx")]
    [InlineData("x.rtf", "x.docx")]
    [InlineData("already.docx", "already.docx")]
    [InlineData("macro.docm", "macro.docm")]
    [InlineData("no-ext", "no-ext.docx")]
    [InlineData("  spaced.md  ", "spaced.docx")]
    public void NormalizeWordCreateOutputPath_CoercesCommonExtensions(string input, string expectedSuffix)
    {
        var actual = OpenXmlHelpers.NormalizeWordCreateOutputPath(input);
        Assert.EndsWith(expectedSuffix, actual, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(Path.GetExtension(expectedSuffix), ".docm", StringComparison.OrdinalIgnoreCase))
            Assert.Equal(expectedSuffix, Path.GetFileName(actual), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeWordCreateOutputPath_LeavesUnknownExtensionUnchanged()
    {
        var actual = OpenXmlHelpers.NormalizeWordCreateOutputPath("file.pdf");
        Assert.Equal("file.pdf", actual);
    }

    [Theory]
    [InlineData("book.md", "book.xlsx")]
    [InlineData("sheet.csv", "sheet.xlsx")]
    [InlineData("legacy.xls", "legacy.xlsx")]
    [InlineData("macro.xlsm", "macro.xlsm")]
    [InlineData("newbook", "newbook.xlsx")]
    public void NormalizeExcelCreateOutputPath_CoercesCommonExtensions(string input, string expectedFileName)
    {
        var actual = OpenXmlHelpers.NormalizeExcelCreateOutputPath(input);
        Assert.Equal(expectedFileName, Path.GetFileName(actual), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeExcelCreateOutputPath_LeavesUnknownExtensionUnchanged()
    {
        Assert.Equal("a.pdf", OpenXmlHelpers.NormalizeExcelCreateOutputPath("a.pdf"));
    }

    [Theory]
    [InlineData("deck.md", "deck.pptx")]
    [InlineData("old.ppt", "old.pptx")]
    [InlineData("withmacro.pptm", "withmacro.pptm")]
    [InlineData("slides", "slides.pptx")]
    public void NormalizePptCreateOutputPath_CoercesCommonExtensions(string input, string expectedFileName)
    {
        var actual = OpenXmlHelpers.NormalizePptCreateOutputPath(input);
        Assert.Equal(expectedFileName, Path.GetFileName(actual), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePptCreateOutputPath_LeavesUnknownExtensionUnchanged()
    {
        Assert.Equal("x.xlsx", OpenXmlHelpers.NormalizePptCreateOutputPath("x.xlsx"));
    }

    [Theory]
    [InlineData("out.md", "out.pdf")]
    [InlineData("doc.txt", "doc.pdf")]
    [InlineData("merged", "merged.pdf")]
    [InlineData("ok.pdf", "ok.pdf")]
    public void NormalizePdfOutputPath_CoercesCommonExtensions(string input, string expectedFileName)
    {
        var actual = OpenXmlHelpers.NormalizePdfOutputPath(input);
        Assert.Equal(expectedFileName, Path.GetFileName(actual), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizePdfOutputPath_LeavesUnknownExtensionUnchanged()
    {
        Assert.Equal("x.docx", OpenXmlHelpers.NormalizePdfOutputPath("x.docx"));
    }

    [Theory]
    [InlineData("demo.pptx", true, null)]
    [InlineData("demo.PPTX", true, null)]
    [InlineData("demo.pptm", true, null)]
    [InlineData("C:\\Files\\a.pptm", true, null)]
    [InlineData("demo.ppt", false, "[错误] 暂不支持 .ppt 格式")]
    [InlineData("demo.pdf", false, "[错误] 仅支持 .pptx 或 .pptm")]
    [InlineData("demo", false, "[错误] 文件无扩展名")]
    [InlineData("", false, "[错误] 文件无扩展名")]
    public void ValidatePptExtension_ReturnsExpected(string filePath, bool expectedValid, string? expectedErrorContains)
    {
        var valid = OpenXmlHelpers.ValidatePptExtension(filePath, out var errorMessage);
        Assert.Equal(expectedValid, valid);
        if (expectedErrorContains != null)
            Assert.NotNull(errorMessage);
        if (expectedValid)
            Assert.Null(errorMessage);
        else if (expectedErrorContains != null)
            Assert.Contains(expectedErrorContains, errorMessage ?? "");
    }

    [Theory]
    [InlineData("a.txt", true, null)]
    [InlineData("b.MD", true, null)]
    [InlineData("c.markdown", true, null)]
    [InlineData("d.json", true, null)]
    [InlineData("e.csv", true, null)]
    [InlineData("C:\\x\\data.JSON", true, null)]
    [InlineData("f.exe", false, "[错误] 不支持")]
    [InlineData("g.pdf", false, "[错误] 不支持")]
    [InlineData("", false, "[错误] 文件路径为空")]
    [InlineData("   ", false, "[错误] 文件路径为空")]
    [InlineData("noext", false, "[错误] 文件无扩展名")]
    public void ValidateTextFileExtension_ReturnsExpected(string filePath, bool expectedValid, string? expectedErrorContains)
    {
        var valid = OpenXmlHelpers.ValidateTextFileExtension(filePath, out var errorMessage);
        Assert.Equal(expectedValid, valid);
        if (expectedValid)
            Assert.Null(errorMessage);
        else
            Assert.Contains(expectedErrorContains!, errorMessage ?? "");
    }
}
