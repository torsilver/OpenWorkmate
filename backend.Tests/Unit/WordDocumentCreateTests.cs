using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public sealed class WordDocumentCreateTests
{
    [Fact]
    public void WordDocumentCreate_omitted_title_uses_file_stem_as_heading()
    {
        var stem = $"taskly_wdc_{Guid.NewGuid():N}";
        var docPath = Path.Combine(Path.GetTempPath(), $"{stem}.docx");
        try
        {
            var plugin = new WordPlugin();
            var r = plugin.WordDocumentCreate(docPath, title: "", paragraphs: "");
            Assert.Contains("已创建文档", r);
            Assert.Contains(stem, r);

            using var read = WordprocessingDocument.Open(docPath, false);
            var body = read.MainDocumentPart!.Document!.Body!;
            var firstPara = body.Elements<Paragraph>().FirstOrDefault();
            Assert.NotNull(firstPara);
            Assert.Equal(stem, firstPara.InnerText.Trim());
        }
        finally
        {
            try { File.Delete(docPath); } catch { /* ignore */ }
        }
    }
}
