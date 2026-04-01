using System.Runtime.InteropServices;
using OfficeCopilot.Server.Plugins;
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
}
