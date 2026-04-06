using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

/// <summary>PptPlugin OpenXml 链式操作（临时目录，测完删除）。</summary>
public class PptPluginOpenXmlTests : IDisposable
{
    private static JsonElement JBool(bool v) => JsonSerializer.SerializeToElement(v);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppt_plugin_tests_" + Guid.NewGuid().ToString("N"));

    public PptPluginOpenXmlTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch
        {
            /* ignore */
        }
    }

    [Fact]
    public void DocumentCreate_List_Write_Insert_Reorder_Notes_Table()
    {
        var p = new PptPlugin();
        var path = Path.Combine(_dir, "chain.pptx");
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("共 1", p.PptSlidesList(path));
        Assert.Contains("形状列表", p.PptSlideRead(path, 1, JBool(true)));
        Assert.Contains("成功", p.PptSlideWrite(path, 1, "title", "封面"));
        Assert.Contains("成功", p.PptSlideInsert(path, 1, "第二页标题", "要点一\n要点二\n要点三"));
        Assert.Contains("共 2", p.PptSlidesList(path));
        var slide2 = p.PptSlideRead(path, 2, JBool(false));
        Assert.Contains("第二页标题", slide2);
        Assert.Contains("要点一", slide2);
        Assert.Contains("成功", p.PptSlidesReorder(path, "2,1"));
        Assert.Contains("共 2", p.PptSlidesList(path));
        Assert.Contains("成功", p.PptNotesWrite(path, 1, "第一页备注"));
        Assert.Contains("备注", p.PptNotesRead(path, 1));
        Assert.Contains("成功", p.PptTableCreate(path, 1, 2, 3));
        Assert.Contains("成功", p.PptTableWriteCells(path, 1, "A,B,C|1,2,3"));
    }

    [Fact]
    public void SlideInsert_BodyText_WithPipe_NormalizesToMultipleParagraphs()
    {
        var path = Path.Combine(_dir, "pipe_body.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideInsert(path, 0, "标题", "行一|行二|行三"));
        var slide1 = p.PptSlideRead(path, 1, JBool(false));
        Assert.Contains("标题", slide1);
        Assert.Contains("行一", slide1);
        Assert.Contains("行二", slide1);
        Assert.Contains("行三", slide1);
    }

    [Fact]
    public void InsertSlide_AfterSlide1Write_SingleLineBody_StillHasText()
    {
        var path = Path.Combine(_dir, "after_write.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideWrite(path, 1, "title", "封面"));
        Assert.Contains("成功", p.PptSlideInsert(path, 1, "第二页标题", "要点一"));
        var slide1 = p.PptSlideRead(path, 1, JBool(false));
        Assert.Contains("封面", slide1);
        using (var doc = PresentationDocument.Open(path, false))
        {
            var pres = doc.PresentationPart!;
            var slideIds = pres.Presentation!.SlideIdList!.Elements<SlideId>().ToList();
            Assert.Equal(2, slideIds.Count);
            var rel0 = slideIds[0].RelationshipId?.Value;
            var rel1 = slideIds[1].RelationshipId?.Value;
            Assert.False(string.IsNullOrEmpty(rel0));
            Assert.False(string.IsNullOrEmpty(rel1));
            var sp1 = (SlidePart)pres.GetPartById(rel0!);
            var sp2 = (SlidePart)pres.GetPartById(rel1!);
            Assert.NotEqual(sp1.Uri.ToString(), sp2.Uri.ToString());
            Assert.Contains("第二页标题", sp2.Slide!.OuterXml);
            Assert.Contains("要点一", sp2.Slide!.OuterXml);
        }

        var slide2ViaPluginRead = p.PptSlideRead(path, 2, JBool(false));
        Assert.Contains("第二页标题", slide2ViaPluginRead);
        Assert.Contains("要点一", slide2ViaPluginRead);
    }

    [Fact]
    public void InsertSlide_RawPartXml_ContainsInsertedText()
    {
        var path = Path.Combine(_dir, "raw_insert.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideInsert(path, 1, "第二页标题", "要点一"));
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart!;
        var slideIds = pres.Presentation!.SlideIdList!.Elements<SlideId>().ToList();
        Assert.Equal(2, slideIds.Count);
        var rid = slideIds[1].RelationshipId?.Value ?? throw new InvalidOperationException("missing rId");
        var slide2Part = (SlidePart)pres.GetPartById(rid);
        var xml = slide2Part.Slide!.OuterXml;
        Assert.Contains("第二页标题", xml);
        Assert.Contains("要点一", xml);
    }

    [Fact]
    public void SlideDuplicate_TextOnly_Succeeds()
    {
        var p = new PptPlugin();
        var path = Path.Combine(_dir, "dup.pptx");
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideDuplicate(path, 1));
        Assert.Contains("共 2", p.PptSlidesList(path));
    }

    /// <summary>含嵌入图的幻灯片经 OpenXmlPowerTools 复制后仍为 2 页且包可校验。</summary>
    [Fact]
    public void SlideDuplicate_WithEmbeddedImage_Succeeds()
    {
        var png = Path.Combine(_dir, "dup_slide_png.png");
        File.WriteAllBytes(png, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var path = Path.Combine(_dir, "dup_with_img.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideImageAdd(path, 1, png));
        Assert.Contains("成功", p.PptSlideDuplicate(path, 1));
        Assert.Contains("共 2", p.PptSlidesList(path));
        using var doc = PresentationDocument.Open(path, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Microsoft365);
        var errors = validator.Validate(doc).ToList();
        Assert.Empty(errors);
    }

    /// <summary>超链接须写在 a:rPr/a:hlinkClick；写在 a:r 直下 PowerPoint 不生效。</summary>
    [Fact]
    public void HyperlinkAdd_HlinkClick_IsChildOfRunProperties()
    {
        var p = new PptPlugin();
        var path = Path.Combine(_dir, "hyperlink.pptx");
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideWrite(path, 1, "title", "Link me"));
        Assert.Contains("成功", p.PptHyperlinkAdd(path, 1, "https://example.com/", 1, ""));
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart!;
        var slideId = pres.Presentation!.SlideIdList!.Elements<SlideId>().First();
        var slidePart = (SlidePart)pres.GetPartById(slideId.RelationshipId!.Value);
        var hlinks = slidePart.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.HyperlinkOnClick>().ToList();
        Assert.NotEmpty(hlinks);
        Assert.All(hlinks, h => Assert.IsType<DocumentFormat.OpenXml.Drawing.RunProperties>(h.Parent));
    }

    /// <summary>插入图片后须通过 OpenXml 架构校验；缺 p:nvPr 时 PowerPoint 会提示修复。</summary>
    [Fact]
    public void SlideImageAdd_PassesOpenXmlValidation()
    {
        var png = Path.Combine(_dir, "tiny.png");
        File.WriteAllBytes(png, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var path = Path.Combine(_dir, "with_img.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideImageAdd(path, 1, png));
        using var doc = PresentationDocument.Open(path, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Microsoft365);
        var errors = validator.Validate(doc).ToList();
        Assert.Empty(errors);
    }

    /// <summary>创建 → 插入第 2 页 → 写第 1 页：验证写第 1 页后第 2 页中文仍可读（排除操作顺序假象）。</summary>
    [Fact]
    public void WriteSlide1_AfterInsert_PreservesSlide2Chinese()
    {
        var path = Path.Combine(_dir, "insert_then_write.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideInsert(path, 1, "第二页标题", "要点A\n要点B"));
        Assert.Contains("成功", p.PptSlideWrite(path, 1, "title", "封面"));
        var slide2 = p.PptSlideRead(path, 2, JBool(false));
        Assert.Contains("第二页标题", slide2);
        Assert.Contains("要点A", slide2);
        Assert.Contains("要点B", slide2);
    }

    /// <summary>创建 → 插入第 2 页（中文）→ 第 1 页写纯 ASCII：验证非中文写入是否仍导致第 2 页损坏。</summary>
    [Fact]
    public void WriteSlide1_AsciiOnly_AfterInsert_PreservesSlide2Chinese()
    {
        var path = Path.Combine(_dir, "ascii_then_read.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideInsert(path, 1, "第二页标题", "要点一"));
        Assert.Contains("成功", p.PptSlideWrite(path, 1, "title", "Cover"));
        var slide2 = p.PptSlideRead(path, 2, JBool(false));
        Assert.Contains("第二页标题", slide2);
        Assert.Contains("要点一", slide2);
    }

    /// <summary>从 zip 内抽查 slide2.xml，确认 &lt;a:t&gt; 中中文未被截断（UTF-8 完整）。</summary>
    [Fact]
    public void InsertThenWrite_ZipSlide2Xml_ContainsFullChinese()
    {
        var path = Path.Combine(_dir, "zip_check.pptx");
        var p = new PptPlugin();
        Assert.Contains("成功", p.PptDocumentCreate(path));
        Assert.Contains("成功", p.PptSlideWrite(path, 1, "title", "封面"));
        Assert.Contains("成功", p.PptSlideInsert(path, 1, "第二页标题", "要点一"));
        string? slide2Entry = null;
        using (var zip = ZipFile.OpenRead(path))
        {
            // 勿用 Contains("slide2.xml")：会误匹配 ppt/slides/_rels/slide2.xml.rels
            slide2Entry = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/'))
                .FirstOrDefault(n => n.EndsWith("slides/slide2.xml", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(slide2Entry);
            using var stream = zip.GetEntry(slide2Entry)!.Open();
            using var reader = new StreamReader(stream);
            var xml = reader.ReadToEnd();
            Assert.Contains("第二页标题", xml);
            Assert.Contains("要点一", xml);
        }
        var slide2Read = p.PptSlideRead(path, 2, JBool(false));
        Assert.Contains("第二页标题", slide2Read);
        Assert.Contains("要点一", slide2Read);
    }
}
