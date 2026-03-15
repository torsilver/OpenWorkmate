using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class OpenXmlHelpersTests
{
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
