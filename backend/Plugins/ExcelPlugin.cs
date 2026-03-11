using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Plugins;

public sealed class ExcelPlugin
{
    [KernelFunction("list_excel_sheets")]
    [Description("列出指定 Excel 文件(.xlsx)中所有工作表的名称。")]
    public string ListSheets(
        [Description("Excel 文件的完整路径，例如 D:\\data\\sales.xlsx")] string filePath)
    {
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var sheets = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>();
            var names = sheets.Select(s => s.Name?.Value ?? "(unnamed)").ToList();
            return $"共 {names.Count} 个工作表: {string.Join(", ", names)}";
        }
        catch (Exception ex)
        {
            return $"[错误] 无法打开文件: {ex.Message}";
        }
    }

    [KernelFunction("read_excel_range")]
    [Description("读取 Excel 文件中指定工作表、指定区域的数据。返回以制表符分隔的文本。")]
    public string ReadRange(
        [Description("Excel 文件的完整路径")] string filePath,
        [Description("工作表名称，留空则读取第一个工作表")] string sheetName = "",
        [Description("起始单元格，例如 A1")] string startCell = "A1",
        [Description("结束单元格，例如 D10。留空则自动检测数据范围")] string endCell = "")
    {
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var wbPart = doc.WorkbookPart!;
            var sheet = string.IsNullOrEmpty(sheetName)
                ? wbPart.Workbook.Sheets!.Elements<Sheet>().First()
                : wbPart.Workbook.Sheets!.Elements<Sheet>()
                    .FirstOrDefault(s => s.Name?.Value == sheetName)
                  ?? throw new Exception($"找不到工作表: {sheetName}");

            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var sheetData = wsPart.Worksheet.Elements<SheetData>().First();
            var sst = wbPart.SharedStringTablePart?.SharedStringTable;

            var (startRow, startCol) = ParseCellRef(startCell);
            var rows = sheetData.Elements<Row>().ToList();

            int endRow, endCol;
            if (!string.IsNullOrEmpty(endCell))
            {
                (endRow, endCol) = ParseCellRef(endCell);
            }
            else
            {
                endRow = rows.Count > 0 ? (int)rows.Max(r => r.RowIndex!.Value) : startRow;
                endCol = rows.Count > 0
                    ? rows.SelectMany(r => r.Elements<Cell>()).Max(c => GetColIndex(c.CellReference!.Value!))
                    : startCol;
                endRow = Math.Min(endRow, startRow + 99);
            }

            var sb = new StringBuilder();
            for (int r = startRow; r <= endRow; r++)
            {
                var row = rows.FirstOrDefault(x => x.RowIndex?.Value == (uint)r);
                var cells = new List<string>();
                for (int c = startCol; c <= endCol; c++)
                {
                    var colLetter = GetColLetter(c);
                    var cellRef = $"{colLetter}{r}";
                    var cell = row?.Elements<Cell>()
                        .FirstOrDefault(x => x.CellReference?.Value == cellRef);
                    cells.Add(GetCellValue(cell, sst));
                }
                sb.AppendLine(string.Join('\t', cells));
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"[错误] 读取失败: {ex.Message}";
        }
    }

    [KernelFunction("write_excel_data")]
    [Description("向 Excel 文件的指定工作表和起始单元格写入数据。数据以 JSON 二维数组格式传入。如果文件不存在会自动创建。")]
    public string WriteData(
        [Description("Excel 文件的完整路径")] string filePath,
        [Description("JSON 二维数组格式的数据，例如 [[\"姓名\",\"年龄\"],[\"张三\",25]]")] string jsonData,
        [Description("工作表名称，留空则使用第一个或创建 Sheet1")] string sheetName = "",
        [Description("起始单元格，例如 A1")] string startCell = "A1")
    {
        try
        {
            var data = JsonSerializer.Deserialize<List<List<JsonElement>>>(jsonData)
                ?? throw new Exception("JSON 数据解析失败");

            bool isNew = !File.Exists(filePath);
            using var doc = isNew
                ? SpreadsheetDocument.Create(filePath, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook)
                : SpreadsheetDocument.Open(filePath, true);

            WorkbookPart wbPart;
            if (isNew)
            {
                wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook(new Sheets());
            }
            else
            {
                wbPart = doc.WorkbookPart!;
            }

            var targetSheetName = string.IsNullOrEmpty(sheetName) ? "Sheet1" : sheetName;
            var existingSheet = wbPart.Workbook.Sheets!.Elements<Sheet>()
                .FirstOrDefault(s => s.Name?.Value == targetSheetName);

            WorksheetPart wsPart;
            if (existingSheet != null)
            {
                wsPart = (WorksheetPart)wbPart.GetPartById(existingSheet.Id!.Value!);
            }
            else
            {
                wsPart = wbPart.AddNewPart<WorksheetPart>();
                wsPart.Worksheet = new Worksheet(new SheetData());
                var sheets = wbPart.Workbook.Sheets!;
                uint newId = sheets.Elements<Sheet>().Any()
                    ? sheets.Elements<Sheet>().Max(s => s.SheetId!.Value) + 1
                    : 1;
                sheets.Append(new Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = newId,
                    Name = targetSheetName
                });
            }

            var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()!;
            var (startRow, startCol) = ParseCellRef(startCell);

            for (int r = 0; r < data.Count; r++)
            {
                uint rowIdx = (uint)(startRow + r);
                var row = sheetData.Elements<Row>().FirstOrDefault(x => x.RowIndex?.Value == rowIdx);
                if (row == null)
                {
                    row = new Row { RowIndex = rowIdx };
                    sheetData.Append(row);
                }

                for (int c = 0; c < data[r].Count; c++)
                {
                    var colLetter = GetColLetter(startCol + c);
                    var cellRef = $"{colLetter}{rowIdx}";
                    var cell = row.Elements<Cell>().FirstOrDefault(x => x.CellReference?.Value == cellRef);
                    if (cell == null)
                    {
                        cell = new Cell { CellReference = cellRef };
                        row.Append(cell);
                    }

                    var val = data[r][c];
                    if (val.ValueKind == JsonValueKind.Number)
                    {
                        cell.CellValue = new CellValue(val.GetDouble().ToString());
                        cell.DataType = CellValues.Number;
                    }
                    else
                    {
                        cell.CellValue = new CellValue(val.ToString());
                        cell.DataType = CellValues.String;
                    }
                }
            }

            wsPart.Worksheet.Save();
            wbPart.Workbook.Save();

            return $"成功写入 {data.Count} 行数据到 {targetSheetName}!{startCell} ({filePath})";
        }
        catch (Exception ex)
        {
            return $"[错误] 写入失败: {ex.Message}";
        }
    }

    // ── helpers ──

    private static string GetCellValue(Cell? cell, SharedStringTable? sst)
    {
        if (cell?.CellValue == null) return "";
        var val = cell.CellValue.Text;
        if (cell.DataType?.Value == CellValues.SharedString && sst != null)
        {
            if (int.TryParse(val, out var idx))
                return sst.ElementAt(idx).InnerText;
        }
        return val;
    }

    private static (int row, int col) ParseCellRef(string cellRef)
    {
        int i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;
        var colStr = cellRef[..i].ToUpperInvariant();
        var rowStr = cellRef[i..];
        return (int.Parse(rowStr), GetColIndex(colStr));
    }

    private static int GetColIndex(string cellRef)
    {
        var col = "";
        foreach (var ch in cellRef)
        {
            if (char.IsLetter(ch)) col += char.ToUpper(ch);
            else break;
        }
        int result = 0;
        foreach (var ch in col)
            result = result * 26 + (ch - 'A' + 1);
        return result;
    }

    private static string GetColLetter(int colIndex)
    {
        var result = "";
        while (colIndex > 0)
        {
            colIndex--;
            result = (char)('A' + colIndex % 26) + result;
            colIndex /= 26;
        }
        return result;
    }
}
