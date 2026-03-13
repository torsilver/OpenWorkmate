using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Plugins;

public sealed class ExcelPlugin
{
    [KernelFunction("excel_sheets_list")]
    [Description("列出 Excel 工作簿中所有工作表名称。filePath 支持环境变量与相对路径（解析到下载目录）。")]
    public string ExcelSheetsList(
        [Description("Excel 文件完整路径，.xlsx 或 .xlsm")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var sheets = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>();
            var names = sheets.Select(s => s.Name?.Value ?? "(unnamed)").ToList();
            return $"共 {names.Count} 个工作表: {string.Join(", ", names)}";
        }
        catch (Exception ex) { return $"[错误] 无法打开文件: {ex.Message}"; }
    }

    [KernelFunction("excel_range_read")]
    [Description("读取指定工作表区域的值或公式，返回制表符分隔文本。大文件可设 maxRows 或 endCell 控制内存；includeFormulas 为 true 时返回公式字符串。")]
    public string ExcelRangeRead(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("起始单元格，如 A1")] string startCell = "A1",
        [Description("结束单元格，如 D10；留空自动检测")] string endCell = "",
        [Description("单次最多读取行数，0 不限制；大文件建议 50000～100000")] int maxRows = 0,
        [Description("是否返回公式而非缓存值")] bool includeFormulas = false)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var wbPart = doc.WorkbookPart!;
            var sheet = string.IsNullOrEmpty(sheetName)
                ? wbPart.Workbook.Sheets!.Elements<Sheet>().First()
                : wbPart.Workbook.Sheets!.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName)
                  ?? throw new Exception($"找不到工作表: {sheetName}");
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var sst = wbPart.SharedStringTablePart?.SharedStringTable;
            var (startRow, startCol) = ParseCellRef(startCell);

            if (maxRows > 0)
                return ReadRangeSax(wsPart, sst, startRow, startCol, endCell, maxRows, includeFormulas);

            var sheetData = wsPart.Worksheet.Elements<SheetData>().First();
            int endRow, endCol;
            if (!string.IsNullOrEmpty(endCell))
                (endRow, endCol) = ParseCellRef(endCell);
            else
            {
                var maxR = 0; var maxC = startCol;
                foreach (var row in sheetData.Elements<Row>())
                {
                    if (row.RowIndex?.Value is { } ri) maxR = Math.Max(maxR, (int)ri);
                    foreach (var c in row.Elements<Cell>())
                        if (c.CellReference?.Value is { } cr) maxC = Math.Max(maxC, GetColIndex(cr));
                }
                endRow = maxR > 0 ? Math.Min(maxR, startRow + 99) : startRow;
                endCol = maxC;
            }

            var sb = new StringBuilder();
            foreach (var row in sheetData.Elements<Row>())
            {
                var r = (int)(row.RowIndex?.Value ?? 0);
                if (r < startRow) continue;
                if (r > endRow) break;
                var cells = new List<string>();
                for (int c = startCol; c <= endCol; c++)
                {
                    var colLetter = GetColLetter(c);
                    var cellRef = $"{colLetter}{r}";
                    var cell = row.Elements<Cell>().FirstOrDefault(x => x.CellReference?.Value == cellRef);
                    cells.Add(includeFormulas ? GetCellFormulaOrValue(cell, sst) : GetCellValue(cell, sst));
                }
                sb.AppendLine(string.Join('\t', cells));
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("excel_range_write")]
    [Description("向指定工作表和起始单元格写入 JSON 二维数组数据；文件不存在则创建。")]
    public string ExcelRangeWrite(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("JSON 二维数组，如 [[\"姓名\",\"年龄\"],[\"张三\",25]]")] string jsonData,
        [Description("工作表名称，留空为 Sheet1")] string sheetName = "",
        [Description("起始单元格，如 A1")] string startCell = "A1")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            var data = JsonSerializer.Deserialize<List<List<JsonElement>>>(jsonData) ?? throw new Exception("JSON 解析失败");
            bool isNew = !File.Exists(filePath);
            var isXlsm = filePath.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase);
            var docType = isXlsm ? SpreadsheetDocumentType.MacroEnabledWorkbook : SpreadsheetDocumentType.Workbook;
            using var doc = isNew ? SpreadsheetDocument.Create(filePath, docType) : SpreadsheetDocument.Open(filePath, true);
            var wbPart = isNew ? doc.AddWorkbookPart() : doc.WorkbookPart!;
            if (isNew) wbPart.Workbook = new Workbook(new Sheets());

            var targetSheetName = string.IsNullOrEmpty(sheetName) ? "Sheet1" : sheetName;
            var existingSheet = wbPart.Workbook.Sheets!.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == targetSheetName);
            WorksheetPart wsPart;
            if (existingSheet != null)
                wsPart = (WorksheetPart)wbPart.GetPartById(existingSheet.Id!.Value!);
            else
            {
                wsPart = wbPart.AddNewPart<WorksheetPart>();
                wsPart.Worksheet = new Worksheet(new SheetData());
                var sheets = wbPart.Workbook.Sheets!;
                uint newId = sheets.Elements<Sheet>().Any() ? sheets.Elements<Sheet>().Max(s => s.SheetId!.Value) + 1 : 1;
                sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = newId, Name = targetSheetName });
            }

            var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()!;
            var (startRow, startCol) = ParseCellRef(startCell);
            for (int r = 0; r < data.Count; r++)
            {
                uint rowIdx = (uint)(startRow + r);
                var row = sheetData.Elements<Row>().FirstOrDefault(x => x.RowIndex?.Value == rowIdx);
                if (row == null) { row = new Row { RowIndex = rowIdx }; sheetData.Append(row); }
                for (int c = 0; c < data[r].Count; c++)
                {
                    var colLetter = GetColLetter(startCol + c);
                    var cellRef = $"{colLetter}{rowIdx}";
                    var cell = row.Elements<Cell>().FirstOrDefault(x => x.CellReference?.Value == cellRef);
                    if (cell == null) { cell = new Cell { CellReference = cellRef }; row.Append(cell); }
                    var val = data[r][c];
                    if (val.ValueKind == JsonValueKind.Number)
                    { cell.CellValue = new CellValue(val.GetDouble().ToString()); cell.DataType = CellValues.Number; }
                    else
                    { cell.CellValue = new CellValue(val.ToString()); cell.DataType = CellValues.String; }
                }
            }
            wsPart.Worksheet.Save();
            wbPart.Workbook.Save();
            return $"已写入 {data.Count} 行到 {targetSheetName}!{startCell}";
        }
        catch (Exception ex) { return $"[错误] 写入失败: {ex.Message}"; }
    }

    [KernelFunction("excel_formula_write")]
    [Description("向指定单元格写入公式字符串。")]
    public string ExcelFormulaWrite(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("公式字符串，如 =SUM(A1:A10)")] string formula,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("单元格地址，如 B2")] string cellRef = "A1")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var wbPart = doc.WorkbookPart!;
            var sheet = string.IsNullOrEmpty(sheetName)
                ? wbPart.Workbook.Sheets!.Elements<Sheet>().First()
                : wbPart.Workbook.Sheets!.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName)
                  ?? throw new Exception($"找不到工作表: {sheetName}");
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()!;
            var (row, col) = ParseCellRef(cellRef);
            var rowElem = sheetData.Elements<Row>().FirstOrDefault(x => x.RowIndex?.Value == (uint)row);
            if (rowElem == null) { rowElem = new Row { RowIndex = (uint)row }; sheetData.Append(rowElem); }
            var cell = rowElem.Elements<Cell>().FirstOrDefault(x => x.CellReference?.Value == cellRef);
            if (cell == null) { cell = new Cell { CellReference = cellRef }; rowElem.Append(cell); }
            cell.CellFormula = new CellFormula(formula.TrimStart('='));
            cell.CellValue = null;
            cell.DataType = null;
            wsPart.Worksheet.Save();
            return $"已写入公式到 {cellRef}";
        }
        catch (Exception ex) { return $"[错误] 写入公式失败: {ex.Message}"; }
    }

    [KernelFunction("excel_cells_merge")]
    [Description("合并指定矩形区域单元格，仅左上角保留内容。")]
    public string ExcelCellsMerge(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("合并区域，如 A1:C1 或 B2:D5")] string range = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供合并区域，如 A1:C1。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var worksheet = wsPart.Worksheet;
            var sheetData = worksheet.GetFirstChild<SheetData>()!;
            var mergeCells = worksheet.Elements<MergeCells>().FirstOrDefault();
            if (mergeCells == null)
            {
                mergeCells = new MergeCells();
                worksheet.InsertAfter(mergeCells, sheetData);
            }
            mergeCells.Append(new MergeCell { Reference = range.Trim() });
            worksheet.Save();
            return $"已合并区域 {range}";
        }
        catch (Exception ex) { return $"[错误] 合并失败: {ex.Message}"; }
    }

    [KernelFunction("excel_cells_unmerge")]
    [Description("取消指定区域的合并。")]
    public string ExcelCellsUnmerge(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("已合并的区域引用，如 A1:C1")] string range = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供要取消合并的区域。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var mergeCells = wsPart.Worksheet.Elements<MergeCells>().FirstOrDefault();
            if (mergeCells == null) return "该工作表中无合并单元格。";
            var toRemove = mergeCells.Elements<MergeCell>().FirstOrDefault(m => (m.Reference?.Value ?? "").Equals(range.Trim(), StringComparison.OrdinalIgnoreCase));
            if (toRemove == null) return $"未找到合并区域 {range}。";
            toRemove.Remove();
            wsPart.Worksheet.Save();
            return $"已取消合并 {range}";
        }
        catch (Exception ex) { return $"[错误] 取消合并失败: {ex.Message}"; }
    }

    [KernelFunction("excel_named_ranges_list")]
    [Description("列出工作簿中所有命名区域（名称与引用）。")]
    public string ExcelNamedRangesList(
        [Description("Excel 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var definedNames = doc.WorkbookPart!.Workbook.DefinedNames;
            if (definedNames == null || !definedNames.Any())
                return "工作簿中无命名区域。";
            var sb = new StringBuilder();
            foreach (var dn in definedNames.Elements<DefinedName>())
                sb.AppendLine($"{dn.Name?.Value ?? ""}\t{dn.Text ?? ""}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("excel_named_range_read")]
    [Description("按命名区域名称读取其引用范围内的数据（值）。")]
    public string ExcelNamedRangeRead(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("命名区域名称")] string name = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(name)) return "[错误] 请提供命名区域名称。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var wbPart = doc.WorkbookPart!;
            var definedName = wbPart.Workbook.DefinedNames?.Elements<DefinedName>()
                .FirstOrDefault(dn => string.Equals(dn.Name?.Value, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (definedName == null) return $"[错误] 未找到命名区域: {name}";
            var refText = (definedName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(refText)) return "该命名区域无有效引用。";
            var (sheetName, range) = ParseDefinedNameRef(refText);
            if (string.IsNullOrEmpty(range)) return $"无法解析引用: {refText}";
            return ExcelRangeRead(filePath, sheetName ?? "", range.Split(':')[0].Trim(), range.Contains(':') ? range.Split(':')[1].Trim() : "", 0, false);
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("excel_named_range_define")]
    [Description("定义或覆盖一个命名区域，引用给定工作表中的区域。")]
    public string ExcelNamedRangeDefine(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("命名区域名称")] string name = "",
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("区域引用，如 A1:D10 或 A1")] string range = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(name)) return "[错误] 请提供命名区域名称。";
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供区域引用。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var wbPart = doc.WorkbookPart!;
            var r = range.Trim();
            string ToAbs(string c)
            {
                int i = 0; while (i < c.Length && char.IsLetter(c[i])) i++;
                return "$" + c[..i] + "$" + c[i..];
            }
            var formulaRef = r.Contains(':')
                ? string.Join(":", r.Split(':').Select(part => ToAbs(part.Trim())))
                : ToAbs(r);
            if (!string.IsNullOrEmpty(sheetName))
                formulaRef = $"'{sheetName}'!{formulaRef}";
            var definedNames = wbPart.Workbook.DefinedNames ?? wbPart.Workbook.AppendChild(new DefinedNames());
            var existing = definedNames.Elements<DefinedName>().FirstOrDefault(dn => string.Equals(dn.Name?.Value, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Text = formulaRef;
            else definedNames.AppendChild(new DefinedName { Name = name.Trim(), Text = formulaRef });
            wbPart.Workbook.Save();
            return $"已定义命名区域: {name} = {formulaRef}";
        }
        catch (Exception ex) { return $"[错误] 定义失败: {ex.Message}"; }
    }

    [KernelFunction("excel_column_width_set")]
    [Description("设置指定列的宽度。width 为字符宽度数。")]
    public string ExcelColumnWidthSet(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("列索引，从 1 开始（A=1）")] int columnIndex = 1,
        [Description("列宽（字符单位）")] double width = 10)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var worksheet = wsPart.Worksheet;
            var columns = worksheet.GetFirstChild<Columns>();
            if (columns == null) { columns = new Columns(); worksheet.InsertBefore(columns, worksheet.GetFirstChild<SheetData>()); }
            var col = columns.Elements<Column>().FirstOrDefault(c => c.Min?.Value == (uint)columnIndex && c.Max?.Value == (uint)columnIndex);
            if (col == null) { col = new Column { Min = (uint)columnIndex, Max = (uint)columnIndex }; columns.Append(col); }
            col.Width = width;
            col.CustomWidth = true;
            worksheet.Save();
            return $"已设置列 {GetColLetter(columnIndex)} 宽度为 {width}。";
        }
        catch (Exception ex) { return $"[错误] 设置失败: {ex.Message}"; }
    }

    [KernelFunction("excel_row_height_set")]
    [Description("设置指定行的高度。height 为磅值。")]
    public string ExcelRowHeightSet(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("行号，从 1 开始")] int rowIndex = 1,
        [Description("行高（磅）")] double height = 15)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()!;
            var row = sheetData.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == (uint)rowIndex);
            if (row == null) { row = new Row { RowIndex = (uint)rowIndex }; sheetData.Append(row); }
            row.Height = height;
            row.CustomHeight = true;
            wsPart.Worksheet.Save();
            return $"已设置第 {rowIndex} 行高度为 {height}。";
        }
        catch (Exception ex) { return $"[错误] 设置失败: {ex.Message}"; }
    }

    [KernelFunction("excel_validations_list")]
    [Description("列出工作表中所有数据验证规则。")]
    public string ExcelValidationsList(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var validations = wsPart.Worksheet.Elements<DataValidations>().FirstOrDefault();
            if (validations == null || !validations.Elements<DataValidation>().Any())
                return "该工作表中无数据验证。";
            var sb = new StringBuilder();
            foreach (var dv in validations.Elements<DataValidation>())
                sb.AppendLine($"范围: {dv.SequenceOfReferences?.InnerText ?? ""}\t类型: {dv.Type?.Value}\t公式1: {dv.Formula1?.Text}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("excel_validation_set")]
    [Description("为区域设置数据验证。type: list|whole|decimal|textLength；formula1 如列表源 \"A,B,C\" 或数值。")]
    public string ExcelValidationSet(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("区域，如 A1:A10")] string range = "",
        [Description("类型: list、whole、decimal、textLength")] string type = "list",
        [Description("公式1，如列表项 \"选项1,选项2\" 或最小值")] string formula1 = "",
        [Description("公式2（用于 between 等），如最大值")] string formula2 = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供区域。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var validations = wsPart.Worksheet.Elements<DataValidations>().FirstOrDefault();
            if (validations == null) { validations = new DataValidations(); wsPart.Worksheet.Append(validations); }
            var dvType = type.Trim().ToLowerInvariant() switch
            {
                "whole" => DataValidationValues.Whole,
                "decimal" => DataValidationValues.Decimal,
                "textLength" => DataValidationValues.TextLength,
                _ => DataValidationValues.List
            };
            var dv = new DataValidation
            {
                Type = dvType,
                AllowBlank = true,
                SequenceOfReferences = new ListValue<StringValue>() { InnerText = range.Trim() }
            };
            if (!string.IsNullOrEmpty(formula1)) dv.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Formula { Text = formula1 });
            if (!string.IsNullOrEmpty(formula2)) dv.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Formula { Text = formula2 });
            validations.Append(dv);
            wsPart.Worksheet.Save();
            return $"已在 {range} 设置数据验证（{type}）。";
        }
        catch (Exception ex) { return $"[错误] 设置失败: {ex.Message}"; }
    }

    [KernelFunction("excel_validation_clear")]
    [Description("清除指定区域的数据验证。")]
    public string ExcelValidationClear(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("要清除的区域，如 A1:A10")] string range = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供区域。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var validations = wsPart.Worksheet.Elements<DataValidations>().FirstOrDefault();
            if (validations == null) return "无数据验证。";
            var toRemove = validations.Elements<DataValidation>().Where(dv => (dv.SequenceOfReferences?.InnerText ?? "").Equals(range.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var dv in toRemove) dv.Remove();
            wsPart.Worksheet.Save();
            return $"已清除 {toRemove.Count} 条验证。";
        }
        catch (Exception ex) { return $"[错误] 清除失败: {ex.Message}"; }
    }

    [KernelFunction("excel_conditional_formats_list")]
    [Description("列出工作表中所有条件格式规则。")]
    public string ExcelConditionalFormatsList(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var cfs = GetWorksheetPart(doc, sheetName).wsPart.Worksheet.Elements<ConditionalFormatting>();
            var sb = new StringBuilder();
            int i = 0;
            foreach (var cf in cfs)
            {
                i++;
                sb.AppendLine($"条件格式 {i}: 范围 {cf.SequenceOfReferences?.InnerText ?? ""}");
            }
            if (i == 0) return "该工作表中无条件格式。";
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("excel_conditional_format_add")]
    [Description("为区域添加条件格式（如单元格值介于两数之间）。")]
    public string ExcelConditionalFormatAdd(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("区域，如 B2:B10")] string range = "",
        [Description("运算符: between、equal、greaterThan、lessThan")] string op = "between",
        [Description("公式1（数值或引用）")] string formula1 = "0",
        [Description("公式2（between 时需要）")] string formula2 = "100")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供区域。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var worksheet = GetWorksheetPart(doc, sheetName).wsPart.Worksheet;
            var cf = new ConditionalFormatting { SequenceOfReferences = new ListValue<StringValue>() };
            cf.SequenceOfReferences.Items.Add(new StringValue(range.Trim()));
            var opVal = op.Trim().ToLowerInvariant() switch
            {
                "equal" => ConditionalFormattingOperatorValues.Equal,
                "greaterThan" => ConditionalFormattingOperatorValues.GreaterThan,
                "lessThan" => ConditionalFormattingOperatorValues.LessThan,
                _ => ConditionalFormattingOperatorValues.Between
            };
            var rule = new ConditionalFormattingRule
            {
                Type = ConditionalFormatValues.CellIs,
                Operator = opVal,
                Priority = new DocumentFormat.OpenXml.Int32Value(worksheet.Elements<ConditionalFormatting>().Count() + 1)
            };
            rule.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Formula(formula1));
            if (opVal == ConditionalFormattingOperatorValues.Between && !string.IsNullOrEmpty(formula2))
                rule.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Formula(formula2));
            cf.AppendChild(rule);
            worksheet.AppendChild(cf);
            worksheet.Save();
            return $"已在 {range} 添加条件格式。";
        }
        catch (Exception ex) { return $"[错误] 添加失败: {ex.Message}"; }
    }

    [KernelFunction("excel_conditional_format_clear")]
    [Description("清除指定区域的条件格式。")]
    public string ExcelConditionalFormatClear(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("区域，如 B2:B10")] string range = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供区域。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var worksheet = GetWorksheetPart(doc, sheetName).wsPart.Worksheet;
            var toRemove = worksheet.Elements<ConditionalFormatting>()
                .Where(cf => (cf.SequenceOfReferences?.InnerText ?? "").Equals(range.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var cf in toRemove) cf.Remove();
            worksheet.Save();
            return $"已清除 {toRemove.Count} 条条件格式。";
        }
        catch (Exception ex) { return $"[错误] 清除失败: {ex.Message}"; }
    }

    [KernelFunction("excel_hyperlink_set")]
    [Description("为单元格设置超链接。")]
    public string ExcelHyperlinkSet(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("单元格，如 A1")] string cellRef = "",
        [Description("链接 URL")] string url = "",
        [Description("显示文本，留空则用 URL")] string displayText = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(cellRef) || string.IsNullOrWhiteSpace(url)) return "[错误] 请提供单元格与 URL。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, sheet) = GetWorksheetPart(doc, sheetName);
            var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()!;
            var (rowIdx, colIdx) = ParseCellRef(cellRef);
            var row = sheetData.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == (uint)rowIdx);
            if (row == null) { row = new Row { RowIndex = (uint)rowIdx }; sheetData.Append(row); }
            var cell = row.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value == cellRef);
            if (cell == null) { cell = new Cell { CellReference = cellRef }; row.Append(cell); }
            var rel = wsPart.AddHyperlinkRelationship(new Uri(url, UriKind.Absolute), true);
            var display = string.IsNullOrEmpty(displayText) ? url : displayText;
            cell.InlineString = new InlineString(new Text(display));
            cell.DataType = CellValues.InlineString;
            cell.CellValue = null;
            var hyperlinks = wsPart.Worksheet.Elements<Hyperlinks>().FirstOrDefault();
            if (hyperlinks == null) { hyperlinks = new Hyperlinks(); wsPart.Worksheet.AppendChild(hyperlinks); }
            hyperlinks.AppendChild(new Hyperlink { Reference = cellRef, Id = rel.Id });
            wsPart.Worksheet.Save();
            return $"已在 {cellRef} 设置超链接。";
        }
        catch (Exception ex) { return $"[错误] 设置失败: {ex.Message}"; }
    }

    [KernelFunction("excel_sheet_add")]
    [Description("在工作簿末尾添加新工作表。")]
    public string ExcelSheetAdd(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("新工作表名称")] string sheetName = "Sheet")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(sheetName)) return "[错误] 请提供工作表名称。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var wbPart = doc.WorkbookPart!;
            var sheets = wbPart.Workbook.Sheets!;
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new Worksheet(new SheetData());
            uint newId = sheets.Elements<Sheet>().Any() ? sheets.Elements<Sheet>().Max(s => s.SheetId!.Value) + 1 : 1;
            sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = newId, Name = sheetName.Trim() });
            wbPart.Workbook.Save();
            return $"已添加工作表「{sheetName}」。";
        }
        catch (Exception ex) { return $"[错误] 添加失败: {ex.Message}"; }
    }

    [KernelFunction("excel_sheet_remove")]
    [Description("按名称删除工作表。")]
    public string ExcelSheetRemove(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("要删除的工作表名称")] string sheetName = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(sheetName)) return "[错误] 请提供要删除的工作表名称。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var wbPart = doc.WorkbookPart!;
            var sheet = wbPart.Workbook.Sheets!.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName);
            if (sheet == null) return $"未找到工作表: {sheetName}";
            wbPart.DeletePart(wbPart.GetPartById(sheet.Id!.Value!));
            sheet.Remove();
            wbPart.Workbook.Save();
            return $"已删除工作表「{sheetName}」。";
        }
        catch (Exception ex) { return $"[错误] 删除失败: {ex.Message}"; }
    }

    [KernelFunction("excel_charts_list")]
    [Description("列出工作簿中各工作表中的图表。")]
    public string ExcelChartsList(
        [Description("Excel 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var sb = new StringBuilder();
            foreach (var sheet in doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>())
            {
                var wsPart = (WorksheetPart)doc.WorkbookPart.GetPartById(sheet.Id!.Value!);
                var chartParts = wsPart.GetPartsOfType<ChartPart>().ToList();
                if (chartParts.Count > 0)
                    sb.AppendLine($"工作表「{sheet.Name?.Value}」: {chartParts.Count} 个图表");
            }
            var result = sb.ToString().TrimEnd();
            return string.IsNullOrEmpty(result) ? "工作簿中无图表。" : result;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    // ── Helpers ──

    private static (WorksheetPart wsPart, Sheet sheet) GetWorksheetPart(SpreadsheetDocument doc, string sheetName)
    {
        var wbPart = doc.WorkbookPart!;
        var sheet = string.IsNullOrEmpty(sheetName)
            ? wbPart.Workbook.Sheets!.Elements<Sheet>().First()
            : wbPart.Workbook.Sheets!.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName)
              ?? throw new Exception($"找不到工作表: {sheetName}");
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
        return (wsPart, sheet);
    }

    private static (string? sheetName, string range) ParseDefinedNameRef(string refText)
    {
        refText = refText.Trim();
        if (refText.Contains('!'))
        {
            var idx = refText.IndexOf('!');
            var sheet = refText[..idx].Trim().Trim('\'').Replace("$", "");
            var rangePart = refText[(idx + 1)..].Replace("$", "").Trim();
            return (sheet, rangePart);
        }
        return (null, refText.Replace("$", ""));
    }

    private static string GetCellValue(Cell? cell, SharedStringTable? sst)
    {
        if (cell?.CellValue == null) return "";
        var val = cell.CellValue.Text;
        if (cell.DataType?.Value == CellValues.SharedString && sst != null && int.TryParse(val, out var idx))
            return sst.ElementAt(idx).InnerText;
        return val;
    }

    private static string GetCellFormulaOrValue(Cell? cell, SharedStringTable? sst)
    {
        if (cell == null) return "";
        if (cell.CellFormula != null && !string.IsNullOrEmpty(cell.CellFormula.Text))
            return "=" + cell.CellFormula.Text;
        return GetCellValue(cell, sst);
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
        foreach (var ch in col) result = result * 26 + (ch - 'A' + 1);
        return result;
    }

    private static string GetColLetter(int colIndex)
    {
        var result = "";
        while (colIndex > 0) { colIndex--; result = (char)('A' + colIndex % 26) + result; colIndex /= 26; }
        return result;
    }

    private static string ReadRangeSax(WorksheetPart wsPart, SharedStringTable? sst, int startRow, int startCol, string endCell, int maxRows, bool includeFormulas)
    {
        int endRow = startRow + maxRows - 1;
        int endCol = 16384;
        if (!string.IsNullOrEmpty(endCell)) { var (er, ec) = ParseCellRef(endCell); endRow = Math.Min(endRow, er); endCol = ec; }
        var sb = new StringBuilder();
        var rowsOutput = 0;
        using var reader = OpenXmlReader.Create(wsPart);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row)) continue;
            var row = (Row)reader.LoadCurrentElement()!;
            var r = (int)(row.RowIndex?.Value ?? 0);
            if (r < startRow) continue;
            if (r > endRow) break;
            var cells = new List<string>();
            for (int c = startCol; c <= endCol; c++)
            {
                var colLetter = GetColLetter(c);
                var cellRef = $"{colLetter}{r}";
                var cell = row.Elements<Cell>().FirstOrDefault(x => x.CellReference?.Value == cellRef);
                cells.Add(includeFormulas ? GetCellFormulaOrValue(cell, sst) : GetCellValue(cell, sst));
            }
            sb.AppendLine(string.Join('\t', cells));
            rowsOutput++;
        }
        var result = sb.ToString().TrimEnd();
        if (rowsOutput >= maxRows) result += $"\n\n（已截断，仅返回前 {rowsOutput} 行。）";
        return result;
    }
}
