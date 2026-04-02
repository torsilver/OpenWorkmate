using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OfficeCopilot.Server.Plugins;
using Xunit;

namespace backend.Tests.Unit;

public class ExcelNamedRangeDefineTests
{
    [Fact]
    public void DefineNamedRange_WhenWorkbookHasCalcPr_insertsDefinedNamesBeforeCalcPr()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taskly-nr-{Guid.NewGuid():N}.xlsx");
        try
        {
            var plugin = new ExcelPlugin();
            Assert.DoesNotContain("[错误]", plugin.ExcelRangeWrite(path, "[[1]]", "Sheet1", "A1"));

            using (var doc = SpreadsheetDocument.Open(path, true))
            {
                var wb = doc.WorkbookPart!.Workbook!;
                wb.AppendChild(new CalculationProperties());
                wb.Save();
            }

            var result = plugin.ExcelNamedRangeDefine(path, "MyRange", "", "A1:B2");
            Assert.DoesNotContain("[错误]", result);

            using var doc2 = SpreadsheetDocument.Open(path, false);
            var wb2 = doc2.WorkbookPart!.Workbook!;
            var children = wb2.ChildElements.ToList();
            var idxNames = children.FindIndex(e => e is DefinedNames);
            var idxCalc = children.FindIndex(e => e is CalculationProperties);
            Assert.True(idxNames >= 0, "应有 definedNames");
            Assert.True(idxCalc >= 0, "测试夹应含 calcPr");
            Assert.True(idxNames < idxCalc, "OOXML 要求 definedNames 在 calcPr 之前，否则 Excel 可能报文件损坏");
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
