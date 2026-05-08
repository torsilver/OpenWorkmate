using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OpenWorkmate.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public sealed class WordImageInsertTests
{
    // 1×1 PNG（有效文件头与结构，便于 ImagePart 写入）
    private static readonly byte[] MinimalPng1x1 =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    [Fact]
    public void WordImageInsert_adds_image_part_and_drawing_and_keeps_paragraph_text()
    {
        var docPath = Path.Combine(Path.GetTempPath(), $"owm_img_{Guid.NewGuid():N}.docx");
        var pngPath = Path.Combine(Path.GetTempPath(), $"owm_1x1_{Guid.NewGuid():N}.png");
        try
        {
            using (var doc = WordprocessingDocument.Create(docPath, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("段落一")))));
                main.Document.Save();
            }

            File.WriteAllBytes(pngPath, MinimalPng1x1);
            var plugin = new WordPlugin();
            var r = plugin.WordImageInsert(docPath, pngPath, paragraphIndex: 1);
            Assert.StartsWith("已在第", r);

            using var read = WordprocessingDocument.Open(docPath, false);
            var mainPart = read.MainDocumentPart!;
            Assert.True(mainPart.ImageParts.Any());
            Assert.Contains("段落一", mainPart.Document!.Body!.InnerText);
            Assert.NotEmpty(mainPart.Document.Body!.Descendants<Drawing>());
        }
        finally
        {
            try { File.Delete(docPath); } catch { /* ignore */ }
            try { File.Delete(pngPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void WordImageInsert_second_image_gets_distinct_drawing_ids()
    {
        var docPath = Path.Combine(Path.GetTempPath(), $"owm_img2_{Guid.NewGuid():N}.docx");
        var pngPath = Path.Combine(Path.GetTempPath(), $"owm_1x1b_{Guid.NewGuid():N}.png");
        try
        {
            using (var doc = WordprocessingDocument.Create(docPath, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("A")))));
                main.Document.Save();
            }

            File.WriteAllBytes(pngPath, MinimalPng1x1);
            var plugin = new WordPlugin();
            Assert.StartsWith("已在第", plugin.WordImageInsert(docPath, pngPath, 1));
            Assert.StartsWith("已在第", plugin.WordImageInsert(docPath, pngPath, 1));

            using var read = WordprocessingDocument.Open(docPath, false);
            var docPrIds = read.MainDocumentPart!.Document!.Descendants
                <DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>()
                .Select(d => d.Id?.Value)
                .Where(v => v != null)
                .Cast<uint>()
                .ToList();
            Assert.Equal(2, docPrIds.Count);
            Assert.Equal(docPrIds.Distinct().Count(), docPrIds.Count);
        }
        finally
        {
            try { File.Delete(docPath); } catch { /* ignore */ }
            try { File.Delete(pngPath); } catch { /* ignore */ }
        }
    }
}
