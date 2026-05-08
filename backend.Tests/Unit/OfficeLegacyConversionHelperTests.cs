using OpenWorkmate.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public sealed class OfficeLegacyConversionHelperTests
{
    [Theory]
    [InlineData(@"C:\a\b\file.doc", OfficeLegacyKind.Word)]
    [InlineData(@"C:\a\b\file.DOT", OfficeLegacyKind.Word)]
    [InlineData("report.xls", OfficeLegacyKind.Excel)]
    [InlineData("slides.ppt", OfficeLegacyKind.PowerPoint)]
    public void TryGetLegacyKind_recognizes_extensions(string path, OfficeLegacyKind expected)
    {
        Assert.True(OfficeLegacyConversionHelper.TryGetLegacyKind(path, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("x.docx")]
    [InlineData("y.xlsx")]
    [InlineData("z.pptx")]
    [InlineData("")]
    [InlineData("noext")]
    public void TryGetLegacyKind_rejects_non_legacy(string path)
    {
        Assert.False(OfficeLegacyConversionHelper.TryGetLegacyKind(path, out _));
    }

    [Fact]
    public void GetExpectedOpenXmlExtension_matches_kind()
    {
        Assert.Equal(".docx", OfficeLegacyConversionHelper.GetExpectedOpenXmlExtension(OfficeLegacyKind.Word));
        Assert.Equal(".xlsx", OfficeLegacyConversionHelper.GetExpectedOpenXmlExtension(OfficeLegacyKind.Excel));
        Assert.Equal(".pptx", OfficeLegacyConversionHelper.GetExpectedOpenXmlExtension(OfficeLegacyKind.PowerPoint));
    }

    [Fact]
    public void ValidateOutputExtension_requires_matching_ext()
    {
        Assert.False(OfficeLegacyConversionHelper.ValidateOutputExtension(@"D:\o.pdf", OfficeLegacyKind.Word, out var err));
        Assert.Contains(".docx", err, StringComparison.Ordinal);
        Assert.True(OfficeLegacyConversionHelper.ValidateOutputExtension(@"D:\o.docx", OfficeLegacyKind.Word, out _));
    }

    [Fact]
    public void TryBuildDefaultOutputPath_builds_stem_converted()
    {
        var temp = Path.Combine(Path.GetTempPath(), "owm_legacy_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var input = Path.Combine(temp, "hello.doc");
        File.WriteAllText(input, "x");
        Assert.True(OfficeLegacyConversionHelper.TryBuildDefaultOutputPath(input, OfficeLegacyKind.Word, out var outp, out var err));
        Assert.Null(err);
        Assert.Equal(Path.Combine(temp, "hello_converted.docx"), outp);
        File.Delete(input);
        Directory.Delete(temp);
    }

    [Fact]
    public void TryBuildDefaultOutputPath_fails_when_default_exists()
    {
        var temp = Path.Combine(Path.GetTempPath(), "owm_legacy_test2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var input = Path.Combine(temp, "dup.xls");
        var blocking = Path.Combine(temp, "dup_converted.xlsx");
        File.WriteAllText(input, "x");
        File.WriteAllText(blocking, "y");
        Assert.False(OfficeLegacyConversionHelper.TryBuildDefaultOutputPath(input, OfficeLegacyKind.Excel, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains("已存在", err, StringComparison.Ordinal);
        File.Delete(input);
        File.Delete(blocking);
        Directory.Delete(temp);
    }
}
