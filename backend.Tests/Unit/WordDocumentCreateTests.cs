using System.Linq;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWorkmate.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class WordDocumentCreateTests
{
    [Fact]
    public void PresetParser_AcceptsCnGov()
    {
        Assert.True(WordDocumentCreatePresetParser.TryParse("cnGovGbt9704", out var p, out var err));
        Assert.Equal(WordDocumentCreatePreset.CnGovGbt9704, p);
        Assert.Null(err);
    }

    [Fact]
    public void PresetParser_Empty_IsDefault()
    {
        Assert.True(WordDocumentCreatePresetParser.TryParse("", out var p, out var err));
        Assert.Equal(WordDocumentCreatePreset.Default, p);
        Assert.Null(err);
    }

    [Fact]
    public void PresetParser_Invalid_ReturnsMessage()
    {
        Assert.False(WordDocumentCreatePresetParser.TryParse("bogus", out _, out var err));
        Assert.NotNull(err);
        Assert.Contains("documentPreset", err, StringComparison.Ordinal);
    }

    [Fact]
    public void WordDocumentCreate_CnGov_PageTopMarginDiffersFromDefault()
    {
        var id = Guid.NewGuid().ToString("N");
        var pathDef = Path.Combine(Path.GetTempPath(), $"owm_wm_def_{id}.docx");
        var pathGov = Path.Combine(Path.GetTempPath(), $"owm_wm_gov_{id}.docx");
        var plugin = new WordPlugin(NullLogger<WordPlugin>.Instance);
        Assert.Contains("已创建", plugin.WordDocumentCreate(pathDef, "T", JsonSerializer.SerializeToElement(new[] { "|a" }), "default"));
        Assert.Contains("已创建", plugin.WordDocumentCreate(pathGov, "T", JsonSerializer.SerializeToElement(new[] { "|a" }), "cnGovGbt9704"));
        try
        {
            var topDef = ReadLastSectionTopTwips(pathDef);
            var topGov = ReadLastSectionTopTwips(pathGov);
            Assert.Equal(1440, topDef);
            Assert.InRange(topGov, 2000, 2200);
            Assert.NotEqual(topDef, topGov);
        }
        finally
        {
            TryDelete(pathDef);
            TryDelete(pathGov);
        }
    }

    [Fact]
    public void WordDocumentCreate_ArrayParagraphs_MultipleBlocks()
    {
        var path = Path.Combine(Path.GetTempPath(), "owm_word_arr_" + Guid.NewGuid().ToString("N") + ".docx");
        var plugin = new WordPlugin(NullLogger<WordPlugin>.Instance);
        try
        {
            Assert.Contains("已创建", plugin.WordDocumentCreate(path, "T", JsonSerializer.SerializeToElement(new[] { "# 标题", "正文一段", "- 列表项" }), "default"));
            using var doc = WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            Assert.NotNull(body);
            var paras = body.Elements<Paragraph>().ToList();
            Assert.True(paras.Count >= 4, "标题 + 至少三段正文");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void WordDocumentCreate_NullOrEmptyParagraphs_OnlyTitle()
    {
        var id = Guid.NewGuid().ToString("N");
        var pathNull = Path.Combine(Path.GetTempPath(), $"owm_word_empty_null_{id}.docx");
        var pathEmpty = Path.Combine(Path.GetTempPath(), $"owm_word_empty_arr_{id}.docx");
        var plugin = new WordPlugin(NullLogger<WordPlugin>.Instance);
        try
        {
            Assert.Contains("已创建", plugin.WordDocumentCreate(pathNull, "仅标题", null, "default"));
            Assert.Contains("已创建", plugin.WordDocumentCreate(pathEmpty, "仅标题", JsonSerializer.SerializeToElement(Array.Empty<string>()), "default"));
            foreach (var p in new[] { pathNull, pathEmpty })
            {
                using var doc = WordprocessingDocument.Open(p, false);
                var body = doc.MainDocumentPart?.Document?.Body;
                Assert.NotNull(body);
                var paras = body.Elements<Paragraph>().ToList();
                Assert.True(paras.Count >= 1);
                Assert.Contains("仅标题", paras[0].InnerText, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDelete(pathNull);
            TryDelete(pathEmpty);
        }
    }

    private static int ReadLastSectionTopTwips(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        Assert.NotNull(body);
        var sect = body.Elements<SectionProperties>().LastOrDefault();
        Assert.NotNull(sect);
        var m = sect.GetFirstChild<PageMargin>();
        Assert.NotNull(m?.Top);
        return m.Top!.Value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
