using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class ExcelConditionalFormatAddTests
{
    [Fact]
    public void ConditionalFormatAdd_WhenWorksheetHasPageMargins_insertsConditionalFormattingBeforePageMargins()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskly-cf-{Guid.NewGuid():N}.xlsx");
        try
        {
            var plugin = new ExcelPlugin();
            Assert.DoesNotContain("[错误]", plugin.ExcelRangeWrite(path, "[[1],[2]]", "Sheet1", "A1"));

            using (var doc = SpreadsheetDocument.Open(path, true))
            {
                var ws = doc.WorkbookPart!.WorksheetParts.First().Worksheet!;
                ws.AppendChild(new PageMargins());
                ws.Save();
            }

            var result = plugin.ExcelConditionalFormatAdd(path, "Sheet1", "A1:A2", "between", "0", "10");
            Assert.DoesNotContain("[错误]", result);

            using var doc2 = SpreadsheetDocument.Open(path, false);
            var ws2 = doc2.WorkbookPart!.WorksheetParts.First().Worksheet!;
            var children = ws2.ChildElements.ToList();
            var idxCf = children.FindIndex(e => e is ConditionalFormatting);
            var idxPm = children.FindIndex(e => e is PageMargins);
            Assert.True(idxCf >= 0, "应有 conditionalFormatting");
            Assert.True(idxPm >= 0, "测试夹应含 pageMargins");
            Assert.True(idxCf < idxPm, "OOXML 要求 conditionalFormatting 在 pageMargins 之前，否则 Excel 可能报 sheet 损坏");

            var rule = ws2.Descendants<ConditionalFormattingRule>().First();
            Assert.NotNull(rule.FormatId?.Value);
            var ss = doc2.WorkbookPart!.WorkbookStylesPart!.Stylesheet!;
            var dxfs = ss.DifferentialFormats;
            Assert.NotNull(dxfs);
            Assert.NotEmpty(dxfs.Elements<DifferentialFormat>());

            using (var zip = ZipFile.OpenRead(path))
            {
                using var sr = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                var sheetXml = sr.ReadToEnd();
                Assert.Contains("dxfId", sheetXml, StringComparison.Ordinal);
                Assert.Contains("cellIs", sheetXml, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
