using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class ExcelValidationSetTests
{
    [Fact]
    public void ValidationSet_WhenWorksheetHasPageMargins_insertsDataValidationsBeforePageMargins()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskly-dv-{Guid.NewGuid():N}.xlsx");
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

            var result = plugin.ExcelValidationSet(path, "Sheet1", "B2:B4", "list", "x,y");
            Assert.DoesNotContain("[错误]", result);

            using var doc2 = SpreadsheetDocument.Open(path, false);
            var ws2 = doc2.WorkbookPart!.WorksheetParts.First().Worksheet!;
            var children = ws2.ChildElements.ToList();
            var idxDv = children.FindIndex(e => e is DataValidations);
            var idxPm = children.FindIndex(e => e is PageMargins);
            Assert.True(idxDv >= 0, "应有 dataValidations");
            Assert.True(idxPm >= 0, "测试夹应含 pageMargins");
            Assert.True(idxDv < idxPm, "OOXML 要求 dataValidations 在 pageMargins 之前，否则 Excel 可能报 sheet 损坏");

            var dv = ws2.Descendants<DataValidation>().First();
            var f1 = dv.Formula1?.Text;
            Assert.Equal("\"x,y\"", f1);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ValidationSet_ListInlineOptions_GetQuotedFormula1SoDropdownHasItems()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskly-dv-q-{Guid.NewGuid():N}.xlsx");
        try
        {
            var plugin = new ExcelPlugin();
            Assert.DoesNotContain("[错误]", plugin.ExcelRangeWrite(path, "[[1]]", "Sheet1", "A1"));
            var setMsg = plugin.ExcelValidationSet(path, "Sheet1", "B1", "list", "优,良,差");
            Assert.DoesNotContain("[错误]", setMsg);

            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = zip.GetEntry("xl/worksheets/sheet1.xml");
                Assert.NotNull(entry);
                using var sr = new StreamReader(entry!.Open());
                var xml = sr.ReadToEnd();
                Assert.Contains("dataValidation", xml, StringComparison.Ordinal);
                Assert.Contains("formula1", xml, StringComparison.Ordinal);
            }

            using var doc = SpreadsheetDocument.Open(path, false);
            var ws = doc.WorkbookPart!.WorksheetParts.First().Worksheet!;
            var f1 = ws.Descendants<DataValidation>().First().Formula1!.Text;
            Assert.Equal("\"优,良,差\"", f1);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ValidationSet_ListCellReference_LeavesFormula1Unquoted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskly-dv-ref-{Guid.NewGuid():N}.xlsx");
        try
        {
            var plugin = new ExcelPlugin();
            Assert.DoesNotContain("[错误]", plugin.ExcelRangeWrite(path, "[[\"a\"],[\"b\"]]", "Sheet1", "A1"));
            Assert.DoesNotContain("[错误]", plugin.ExcelValidationSet(path, "Sheet1", "B1", "list", "A1:A2"));

            using var doc = SpreadsheetDocument.Open(path, false);
            var ws = doc.WorkbookPart!.WorksheetParts.First().Worksheet!;
            var f1 = ws.Descendants<DataValidation>().First().Formula1!.Text;
            Assert.Equal("A1:A2", f1);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
