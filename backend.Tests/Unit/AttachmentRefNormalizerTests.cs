using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AttachmentRefNormalizerTests
{
    [Theory]
    [InlineData("3f95442feb064a4d9d7707a68952e3ae", "attachment:3f95442feb064a4d9d7707a68952e3ae")]
    [InlineData("3F95442FEB064A4D9D7707A68952E3AE", "attachment:3f95442feb064a4d9d7707a68952e3ae")]
    [InlineData(" 3f95442feb064a4d9d7707a68952e3ae ", "attachment:3f95442feb064a4d9d7707a68952e3ae")]
    [InlineData("attachment:3f95442feb064a4d9d7707a68952e3ae", "attachment:3f95442feb064a4d9d7707a68952e3ae")]
    [InlineData("Attachment:3F95442FEB064A4D9D7707A68952E3AE", "attachment:3f95442feb064a4d9d7707a68952e3ae")]
    public void TryNormalize_AcceptsBareOrPrefixedHex(string input, string expected)
    {
        Assert.Equal(expected, AttachmentRefNormalizer.TryNormalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("attachment:")]
    [InlineData("attachment:nothex")]
    [InlineData("attachment:3f95442feb064a4d9d7707a68952e3ae_extra")]
    [InlineData("3f95442feb064a4d9d7707a68952e3a")]
    [InlineData("3f95442feb064a4d9d7707a68952e3aee")]
    [InlineData("zz95442feb064a4d9d7707a68952e3ae")]
    [InlineData("attachment:zz95442feb064a4d9d7707a68952e3ae")]
    public void TryNormalize_RejectsInvalid(string? input)
    {
        Assert.Null(AttachmentRefNormalizer.TryNormalize(input));
    }
}
