using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.ToolInvocation;

namespace OfficeCopilot.Server.Plugins;

public sealed class ExcelPlugin
{
    private readonly ILogger<ExcelPlugin>? _logger;

    public ExcelPlugin(ILogger<ExcelPlugin>? logger = null) => _logger = logger;

    /// <summary>
    /// OOXML 要求 <c>dataValidations</c> 出现在 <c>hyperlinks</c>、<c>pageMargins</c>、<c>drawing</c> 等之前。
    /// 使用 <see cref="OpenXmlCompositeElement.Append"/> 会把它挂到工作表末尾，常见工作簿因此产生非法顺序，Excel 报 sheet.xml 损坏并丢弃工作表内容。
    /// </summary>
    private static readonly HashSet<Type> WorksheetChildTypesAfterDataValidationsSlot = new(
    [
        typeof(Hyperlinks),
        typeof(PrintOptions),
        typeof(PageMargins),
        typeof(PageSetup),
        typeof(HeaderFooter),
        typeof(RowBreaks),
        typeof(ColumnBreaks),
        typeof(CustomProperties),
        typeof(CellWatches),
        typeof(IgnoredErrors),
        typeof(Drawing),
        typeof(LegacyDrawing),
        typeof(LegacyDrawingHeaderFooter),
        typeof(Picture),
        typeof(OleObjects),
        typeof(Controls),
        typeof(TableParts),
        typeof(ExtensionList),
    ]);

    private static OpenXmlElement? FindFirstWorksheetChildAfterDataValidationsSlot(Worksheet worksheet)
    {
        foreach (var child in worksheet.ChildElements)
        {
            if (WorksheetChildTypesAfterDataValidationsSlot.Contains(child.GetType()))
                return child;
        }
        return null;
    }

    /// <summary>ECMA-376：<c>conditionalFormatting</c> 须在 <c>dataValidations</c> 以及 <c>pageMargins</c>、<c>drawing</c> 等之前。</summary>
    private static OpenXmlElement? FindFirstWorksheetChildAfterConditionalFormattingSlot(Worksheet worksheet)
    {
        foreach (var child in worksheet.ChildElements)
        {
            if (child is DataValidations) return child;
            if (WorksheetChildTypesAfterDataValidationsSlot.Contains(child.GetType())) return child;
        }
        return null;
    }

    private static void AppendConditionalFormattingInOrder(Worksheet worksheet, ConditionalFormatting cf)
    {
        var anchor = FindFirstWorksheetChildAfterConditionalFormattingSlot(worksheet);
        var lastCf = worksheet.Elements<ConditionalFormatting>().LastOrDefault();
        if (lastCf != null && (anchor == null || WorksheetChildIsBefore(lastCf, anchor)))
            worksheet.InsertAfter(cf, lastCf);
        else if (anchor != null)
            worksheet.InsertBefore(cf, anchor);
        else
            worksheet.Append(cf);
    }

    /// <summary>无 <c>dxfId</c> + styles 中 <c>dxfs</c> 时，Excel 会接受规则但单元格不会有任何可见高亮。</summary>
    private static DifferentialFormat CreateHighlightDifferentialFormat()
    {
        // Solid 时 fg/bg 同色，否则部分 Excel 版本只套字体、填充异常。不在 dxf 里加边框：有填充时工作表网格线不穿过单元格属 Excel 默认行为。
        const string fillRgb = "FFFFEB9C";
        return new DifferentialFormat(
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold()),
            new Fill(new PatternFill
            {
                PatternType = PatternValues.Solid,
                ForegroundColor = new ForegroundColor { Rgb = fillRgb },
                BackgroundColor = new BackgroundColor { Rgb = fillRgb }
            }));
    }

    private static Stylesheet CreateMinimalStylesheet()
    {
        var fonts = new Fonts(
            new DocumentFormat.OpenXml.Spreadsheet.Font(
                new FontSize { Val = 11 },
                new DocumentFormat.OpenXml.Spreadsheet.Color { Theme = 1 },
                new FontName { Val = "Calibri" })) { Count = 1 };

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 };

        var borders = new Borders(new Border()) { Count = 1 };

        var cellStyleFormats = new CellStyleFormats(new CellFormat
        {
            NumberFormatId = 0,
            FontId = 0,
            FillId = 0,
            BorderId = 0
        }) { Count = 1 };

        // cellXfs 中 FormatId 对应 XML 的 xfId，指向 cellStyleXfs[0]
        var cellFormats = new CellFormats(new CellFormat
        {
            NumberFormatId = 0,
            FontId = 0,
            FillId = 0,
            BorderId = 0,
            FormatId = 0
        }) { Count = 1 };

        var cellStyles = new CellStyles(new CellStyle
        {
            Name = "Normal",
            FormatId = 0,
            BuiltinId = 0
        }) { Count = 1 };

        return new Stylesheet(fonts, fills, borders, cellStyleFormats, cellFormats, cellStyles);
    }

    /// <summary>Excel 常要求存在 <c>cellStyles</c>（如 Normal），且 <c>dxfs</c> 须排在其后，否则 dxf 高亮可能不生效。</summary>
    private static void EnsureCellStyles(Stylesheet stylesheet)
    {
        if (stylesheet.Elements<CellStyles>().Any()) return;
        var cellFormats = stylesheet.Elements<CellFormats>().FirstOrDefault();
        var cs = new CellStyles(new CellStyle
        {
            Name = "Normal",
            FormatId = 0,
            BuiltinId = 0
        }) { Count = 1 };
        if (cellFormats != null)
            stylesheet.InsertAfter(cs, cellFormats);
        else
            stylesheet.Append(cs);
    }

    private static DifferentialFormats EnsureDifferentialFormatsSection(Stylesheet stylesheet)
    {
        var existing = stylesheet.Elements<DifferentialFormats>().FirstOrDefault();
        if (existing != null) return existing;

        EnsureCellStyles(stylesheet);
        var cellStyles = stylesheet.Elements<CellStyles>().First();
        var dxfs = new DifferentialFormats();
        stylesheet.InsertAfter(dxfs, cellStyles);
        return dxfs;
    }

    /// <summary>在 styles 中追加浅黄填充的 <c>dxf</c>，返回供 <see cref="ConditionalFormattingRule"/> 使用的 <c>dxfId</c>（从 0 起的下标）。</summary>
    private static uint AppendHighlightDxfAndGetId(WorkbookPart workbookPart)
    {
        var stylesPart = workbookPart.WorkbookStylesPart ?? workbookPart.AddNewPart<WorkbookStylesPart>();
        var stylesheet = stylesPart.Stylesheet ?? CreateMinimalStylesheet();
        if (stylesPart.Stylesheet == null)
            stylesPart.Stylesheet = stylesheet;

        var dxfs = EnsureDifferentialFormatsSection(stylesheet);
        var nextId = (uint)dxfs.Elements<DifferentialFormat>().Count();
        dxfs.Append(CreateHighlightDifferentialFormat());
        dxfs.Count = (uint)dxfs.Elements<DifferentialFormat>().Count();
        stylesPart.Stylesheet.Save();
        return nextId;
    }

    private static bool WorksheetChildIsBefore(OpenXmlElement? first, OpenXmlElement second)
    {
        if (first == null) return false;
        var parent = second.Parent;
        if (parent == null || first.Parent != parent) return false;
        foreach (var c in parent.ChildElements)
        {
            if (ReferenceEquals(c, first)) return true;
            if (ReferenceEquals(c, second)) return false;
        }
        return false;
    }

    /// <summary>取得或创建 <c>dataValidations</c>，并保证相对其它子元素的顺序符合 ECMA-376。</summary>
    private static DataValidations EnsureDataValidationsElement(Worksheet worksheet)
    {
        var anchor = FindFirstWorksheetChildAfterDataValidationsSlot(worksheet);
        var dv = worksheet.Elements<DataValidations>().FirstOrDefault();
        if (dv == null)
        {
            dv = new DataValidations();
            if (anchor != null)
                worksheet.InsertBefore(dv, anchor);
            else
                worksheet.Append(dv);
            return dv;
        }

        if (anchor != null && !WorksheetChildIsBefore(dv, anchor))
        {
            dv.Remove();
            worksheet.InsertBefore(dv, anchor);
        }

        return dv;
    }

    private static void SyncDataValidationsCount(DataValidations validations)
    {
        var n = validations.Elements<DataValidation>().Count();
        if (n == 0)
        {
            validations.Remove();
            return;
        }

        validations.Count = (uint)n;
    }

    /// <summary>
    /// Excel 列表验证的内联选项在文件中须写成带双引号的公式文本（如 <c>"优,良,差"</c>）。
    /// 只写 <c>优,良,差</c> 时仍会显示下拉箭头，但展开无选项。
    /// </summary>
    private static string FormatDataValidationListFormula1(string formula1)
    {
        var s = formula1.Trim();
        if (s.Length == 0) return s;
        if (s.StartsWith("=", StringComparison.Ordinal)) return s;
        if (s.Contains('!')) return s;
        if (IsLikelyA1StyleCellOrRange(s)) return s;
        if (IsLikelyAsciiExcelDefinedName(s)) return s;
        s = s.Replace('，', ',');
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s;
        var escaped = s.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static bool IsLikelyA1StyleCellOrRange(string s) =>
        Regex.IsMatch(
            s,
            @"^\$?[A-Za-z]{1,3}\$?[1-9]\d*(?::\$?[A-Za-z]{1,3}\$?[1-9]\d*)?$",
            RegexOptions.CultureInvariant);

    /// <summary>仅 ASCII 的命名区域风格标识，避免把 <c>MyList</c> 误包成字面量。</summary>
    private static bool IsLikelyAsciiExcelDefinedName(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (c > 127) return false;
        }

        return Regex.IsMatch(s, @"^[A-Za-z_][\w.]*$", RegexOptions.CultureInvariant);
    }

    [ToolFunction("excel_sheets_list")]
    [Description("列出 Excel 工作簿中所有工作表名称。filePath 支持环境变量与相对路径（相对路径相对当前用户约定目录，多为 Downloads）。")]
    public string ExcelSheetsList(
        [Description("Excel 文件完整路径，.xlsx 或 .xlsm")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var wbPart = RequireWorkbookPart(doc);
            var workbook = RequireWorkbook(wbPart);
            var names = RequireSheets(workbook).Elements<Sheet>().Select(s => s.Name?.Value ?? "(unnamed)").ToList();
            return $"共 {names.Count} 个工作表: {string.Join(", ", names)}";
        }
        catch (Exception ex) { return $"[错误] 无法打开文件: {ex.Message}"; }
    }

    [ToolFunction("excel_range_read")]
    [Description("读取指定工作表区域的值或公式，返回制表符分隔文本。大文件可设 maxRows 或 endCell 控制内存；includeFormulas 为 true 时返回公式字符串。")]
    public string ExcelRangeRead(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("起始单元格，如 A1")] string startCell = "A1",
        [Description("结束单元格，如 D10；留空自动检测")] string endCell = "",
        [Description("单次最多读取行数，0 不限制；大文件建议 50000～100000")] int maxRows = 0,
        [Description("是否返回公式而非缓存值。JSON 布尔或字符串均可。")] JsonElement? includeFormulas = null)
    {
        if (!ToolScalarArgumentParser.TryReadBoolWithDefault(includeFormulas, false, out var includeFormulasValue))
            return "[错误] includeFormulas 无效：请使用 true/false 或字符串 \"true\"/\"false\"。";
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var wbPart = RequireWorkbookPart(doc);
            var workbook = RequireWorkbook(wbPart);
            var sheets = RequireSheets(workbook);
            var sheet = string.IsNullOrEmpty(sheetName)
                ? sheets.Elements<Sheet>().First()
                : sheets.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName)
                  ?? throw new Exception($"找不到工作表: {sheetName}");
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var sst = wbPart.SharedStringTablePart?.SharedStringTable;
            var (startRow, startCol) = ParseCellRef(startCell);

            if (maxRows > 0)
                return ReadRangeSax(wsPart, sst, startRow, startCol, endCell, maxRows, includeFormulasValue);

            var sheetData = RequireWorksheet(wsPart).Elements<SheetData>().First();
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
                    cells.Add(includeFormulasValue ? GetCellFormulaOrValue(cell, sst) : GetCellValue(cell, sst));
                }
                sb.AppendLine(string.Join('\t', cells));
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [ToolFunction("excel_range_write")]
    [Description("向指定工作表和起始单元格写入 JSON 二维数组数据；文件不存在则创建（Open XML 工作簿）。filePath 必须以 .xlsx（推荐）或 .xlsm 结尾，勿用 .md/.txt/.csv/.xls 当作扩展名。路径须对应当前登录用户：优先仅文件名或相对路径；勿用 Public/%PUBLIC% 或臆测用户名目录。")]
    public string ExcelRangeWrite(
        [Description("Excel 路径，必须以 .xlsx（推荐）或 .xlsm 结尾；禁止用 .md、.txt、.csv、旧版 .xls。优先仅文件名或相对路径（当前用户下约定目录，常为 Downloads）；绝对路径用 %USERPROFILE%\\…")] string filePath,
        [Description("合法 JSON 二维数组字符串，双引号键/字符串；示例 [[\"姓名\",\"年龄\"],[\"张三\",25]]。勿用单引号；解析失败时请检查转义与逗号")] string jsonData,
        [Description("工作表名称，留空为 Sheet1")] string sheetName = "",
        [Description("起始单元格，如 A1")] string startCell = "A1")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        var beforeNormalize = filePath;
        filePath = OpenXmlHelpers.NormalizeExcelCreateOutputPath(filePath);
        if (!string.Equals(filePath, beforeNormalize, StringComparison.OrdinalIgnoreCase))
            _logger?.LogInformation("[Excel] excel_range_write normalized path from {Before} to {After}", beforeNormalize, filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(jsonData))
            return "[错误] jsonData 不能为空；请传入 JSON 二维数组字符串（见参数说明中的示例）。";
        try
        {
            List<List<JsonElement>>? data;
            try
            {
                data = JsonSerializer.Deserialize<List<List<JsonElement>>>(jsonData);
            }
            catch (JsonException jex)
            {
                return $"[错误] jsonData 不是合法 JSON 二维数组：{jex.Message}。请使用双引号字符串、检查逗号与反斜杠转义后重试。";
            }

            if (data == null)
                return "[错误] jsonData 解析结果为 null；请传入 JSON 二维数组字符串。";
            bool isNew = !File.Exists(filePath);
            var isXlsm = filePath.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase);
            var docType = isXlsm ? SpreadsheetDocumentType.MacroEnabledWorkbook : SpreadsheetDocumentType.Workbook;
            using var doc = isNew ? SpreadsheetDocument.Create(filePath, docType) : SpreadsheetDocument.Open(filePath, true);
            var wbPart = isNew ? doc.AddWorkbookPart() : RequireWorkbookPart(doc);
            if (isNew) wbPart.Workbook = new Workbook(new Sheets());

            var targetSheetName = string.IsNullOrEmpty(sheetName) ? "Sheet1" : sheetName;
            var workbook = RequireWorkbook(wbPart);
            var existingSheet = RequireSheets(workbook).Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == targetSheetName);
            WorksheetPart wsPart;
            if (existingSheet != null)
                wsPart = (WorksheetPart)wbPart.GetPartById(existingSheet.Id!.Value!);
            else
            {
                wsPart = wbPart.AddNewPart<WorksheetPart>();
                wsPart.Worksheet = new Worksheet(new SheetData());
                var sheets = RequireSheets(workbook);
                uint newId = sheets.Elements<Sheet>().Any() ? sheets.Elements<Sheet>().Max(s => s.SheetId!.Value) + 1 : 1;
                sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = newId, Name = targetSheetName });
            }

            var sheetData = RequireWorksheet(wsPart).GetFirstChild<SheetData>()!;
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
            RequireWorksheet(wsPart).Save();
            RequireWorkbook(wbPart).Save();
            return $"已写入 {data.Count} 行到 {targetSheetName}!{startCell}";
        }
        catch (Exception ex) { return $"[错误] 写入失败: {ex.Message}"; }
    }

    [ToolFunction("excel_formula_write")]
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
            var wbPart = RequireWorkbookPart(doc);
            var workbook = RequireWorkbook(wbPart);
            var sheets = RequireSheets(workbook);
            var sheet = string.IsNullOrEmpty(sheetName)
                ? sheets.Elements<Sheet>().First()
                : sheets.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName)
                  ?? throw new Exception($"找不到工作表: {sheetName}");
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var sheetData = RequireWorksheet(wsPart).GetFirstChild<SheetData>()!;
            var (row, col) = ParseCellRef(cellRef);
            var rowElem = sheetData.Elements<Row>().FirstOrDefault(x => x.RowIndex?.Value == (uint)row);
            if (rowElem == null) { rowElem = new Row { RowIndex = (uint)row }; sheetData.Append(rowElem); }
            var cell = rowElem.Elements<Cell>().FirstOrDefault(x => x.CellReference?.Value == cellRef);
            if (cell == null) { cell = new Cell { CellReference = cellRef }; rowElem.Append(cell); }
            cell.CellFormula = new CellFormula(formula.TrimStart('='));
            cell.CellValue = null;
            cell.DataType = null;
            RequireWorksheet(wsPart).Save();
            return $"已写入公式到 {cellRef}";
        }
        catch (Exception ex) { return $"[错误] 写入公式失败: {ex.Message}"; }
    }

    [ToolFunction("excel_cells_merge")]
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
            var worksheet = RequireWorksheet(wsPart);
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

    [ToolFunction("excel_cells_unmerge")]
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
            var worksheet = RequireWorksheet(wsPart);
            var mergeCells = worksheet.Elements<MergeCells>().FirstOrDefault();
            if (mergeCells == null) return "该工作表中无合并单元格。";
            var toRemove = mergeCells.Elements<MergeCell>().FirstOrDefault(m => (m.Reference?.Value ?? "").Equals(range.Trim(), StringComparison.OrdinalIgnoreCase));
            if (toRemove == null) return $"未找到合并区域 {range}。";
            toRemove.Remove();
            worksheet.Save();
            return $"已取消合并 {range}";
        }
        catch (Exception ex) { return $"[错误] 取消合并失败: {ex.Message}"; }
    }

    [ToolFunction("excel_named_ranges_list")]
    [Description("列出工作簿中所有命名区域（名称与引用）。")]
    public string ExcelNamedRangesList(
        [Description("Excel 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var definedNames = RequireWorkbook(RequireWorkbookPart(doc)).DefinedNames;
            if (definedNames == null || !definedNames.Any())
                return "工作簿中无命名区域。";
            var sb = new StringBuilder();
            foreach (var dn in definedNames.Elements<DefinedName>())
                sb.AppendLine($"{dn.Name?.Value ?? ""}\t{dn.Text ?? ""}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [ToolFunction("excel_named_range_read")]
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
            var wbPart = RequireWorkbookPart(doc);
            var definedName = RequireWorkbook(wbPart).DefinedNames?.Elements<DefinedName>()
                .FirstOrDefault(dn => string.Equals(dn.Name?.Value, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (definedName == null) return $"[错误] 未找到命名区域: {name}";
            var refText = (definedName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(refText)) return "该命名区域无有效引用。";
            var (sheetName, range) = ParseDefinedNameRef(refText);
            if (string.IsNullOrEmpty(range)) return $"无法解析引用: {refText}";
            return ExcelRangeRead(filePath, sheetName ?? "", range.Split(':')[0].Trim(), range.Contains(':') ? range.Split(':')[1].Trim() : "", 0, JsonSerializer.SerializeToElement(false));
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [ToolFunction("excel_named_range_define")]
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
            var wbPart = RequireWorkbookPart(doc);
            var workbook = RequireWorkbook(wbPart);
            var r = range.Trim();
            string ToAbs(string c)
            {
                int i = 0; while (i < c.Length && char.IsLetter(c[i])) i++;
                if (i == 0 || i >= c.Length) throw new Exception($"无效单元格引用: {c}");
                return "$" + c[..i].ToUpperInvariant() + "$" + c[i..];
            }
            var formulaRef = r.Contains(':')
                ? string.Join(":", r.Split(':').Select(part => ToAbs(part.Trim())))
                : ToAbs(r);
            var sheets = RequireSheets(workbook);
            var resolvedSheet = string.IsNullOrEmpty(sheetName)
                ? sheets.Elements<Sheet>().First().Name?.Value ?? throw new Exception("无法解析首个工作表名称。")
                : sheetName;
            var escapedSheet = resolvedSheet.Replace("'", "''", StringComparison.Ordinal);
            var formulaRefFull = $"'{escapedSheet}'!{formulaRef}";
            var definedNames = workbook.DefinedNames ?? InsertDefinedNamesAfterOptionalPredecessors(workbook);
            var existing = definedNames.Elements<DefinedName>().FirstOrDefault(dn => string.Equals(dn.Name?.Value, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Text = formulaRefFull;
            else definedNames.AppendChild(new DefinedName { Name = name.Trim(), Text = formulaRefFull });
            workbook.Save();
            return $"已定义命名区域: {name} = {formulaRefFull}";
        }
        catch (Exception ex) { return $"[错误] 定义失败: {ex.Message}"; }
    }

    [ToolFunction("excel_column_width_set")]
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
            var worksheet = RequireWorksheet(wsPart);
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

    [ToolFunction("excel_row_height_set")]
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
            var worksheet = RequireWorksheet(wsPart);
            var sheetData = worksheet.GetFirstChild<SheetData>()!;
            var row = sheetData.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == (uint)rowIndex);
            if (row == null) { row = new Row { RowIndex = (uint)rowIndex }; sheetData.Append(row); }
            row.Height = height;
            row.CustomHeight = true;
            worksheet.Save();
            return $"已设置第 {rowIndex} 行高度为 {height}。";
        }
        catch (Exception ex) { return $"[错误] 设置失败: {ex.Message}"; }
    }

    [ToolFunction("excel_validations_list")]
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
            var validations = RequireWorksheet(wsPart).Elements<DataValidations>().FirstOrDefault();
            if (validations == null || !validations.Elements<DataValidation>().Any())
                return "该工作表中无数据验证。";
            var sb = new StringBuilder();
            foreach (var dv in validations.Elements<DataValidation>())
                sb.AppendLine($"范围: {dv.SequenceOfReferences?.InnerText ?? ""}\t类型: {dv.Type?.Value}\t公式1: {dv.Formula1?.Text}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [ToolFunction("excel_validation_set")]
    [Description("为区域设置数据验证。type: list|whole|decimal|textLength；list 时 formula1 为英文逗号分隔的选项（如 优,良,差）或区域/名称/=公式，服务端会按 Excel 要求写入带引号的列表源。")]
    public string ExcelValidationSet(
        [Description("Excel 文件完整路径")] string filePath,
        [Description("工作表名称，留空为第一个")] string sheetName = "",
        [Description("区域，如 A1:A10")] string range = "",
        [Description("类型: list、whole、decimal、textLength")] string type = "list",
        [Description("公式1：list 时为逗号分隔选项（建议英文逗号）或 A1:B10、名称、=公式；其它类型为阈值等")] string formula1 = "",
        [Description("公式2（用于 between 等），如最大值")] string formula2 = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(range)) return "[错误] 请提供区域。";
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, true);
            var (wsPart, _) = GetWorksheetPart(doc, sheetName);
            var worksheet = RequireWorksheet(wsPart);
            var validations = EnsureDataValidationsElement(worksheet);
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
            if (!string.IsNullOrEmpty(formula1))
            {
                var f1 = dvType == DataValidationValues.List ? FormatDataValidationListFormula1(formula1) : formula1;
                dv.Formula1 = new Formula1(f1);
            }

            if (!string.IsNullOrEmpty(formula2))
                dv.Formula2 = new Formula2(formula2);
            validations.Append(dv);
            SyncDataValidationsCount(validations);
            worksheet.Save();
            return $"已在 {range} 设置数据验证（{type}）。";
        }
        catch (Exception ex) { return $"[错误] 设置失败: {ex.Message}"; }
    }

    [ToolFunction("excel_validation_clear")]
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
            var worksheet = RequireWorksheet(wsPart);
            var validations = worksheet.Elements<DataValidations>().FirstOrDefault();
            if (validations == null) return "无数据验证。";
            var toRemove = validations.Elements<DataValidation>().Where(dv => (dv.SequenceOfReferences?.InnerText ?? "").Equals(range.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var dv in toRemove) dv.Remove();
            SyncDataValidationsCount(validations);
            worksheet.Save();
            return $"已清除 {toRemove.Count} 条验证。";
        }
        catch (Exception ex) { return $"[错误] 清除失败: {ex.Message}"; }
    }

    [ToolFunction("excel_conditional_formats_list")]
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
            var (wsP, _) = GetWorksheetPart(doc, sheetName);
            var cfs = RequireWorksheet(wsP).Elements<ConditionalFormatting>();
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

    [ToolFunction("excel_conditional_format_add")]
    [Description("为区域添加条件格式（如单元格值介于两数之间）；写入浅黄底+加粗 dxf，满足条件时在 Excel 中可见。比较的是单元格数值：文本数字不会匹配 between/大于等，需先改为数字（如分列或 excel_range_write 写入数值）。")]
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
            var (wsP2, _) = GetWorksheetPart(doc, sheetName);
            var worksheet = RequireWorksheet(wsP2);
            var wbPart = RequireWorkbookPart(doc);
            var dxfId = AppendHighlightDxfAndGetId(wbPart);
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
                FormatId = dxfId,
                Priority = new DocumentFormat.OpenXml.Int32Value(worksheet.Elements<ConditionalFormatting>().Count() + 1)
            };
            rule.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Formula(formula1));
            if (opVal == ConditionalFormattingOperatorValues.Between && !string.IsNullOrEmpty(formula2))
                rule.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Formula(formula2));
            cf.AppendChild(rule);
            AppendConditionalFormattingInOrder(worksheet, cf);
            worksheet.Save();
            return $"已在 {range} 添加条件格式（满足条件时浅黄底加粗）。若打开后仍无高亮，请确认区域内为数值而非文本数字；空单元格不会匹配。";
        }
        catch (Exception ex) { return $"[错误] 添加失败: {ex.Message}"; }
    }

    [ToolFunction("excel_conditional_format_clear")]
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
            var (wsP3, _) = GetWorksheetPart(doc, sheetName);
            var worksheet = RequireWorksheet(wsP3);
            var toRemove = worksheet.Elements<ConditionalFormatting>()
                .Where(cf => (cf.SequenceOfReferences?.InnerText ?? "").Equals(range.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var cf in toRemove) cf.Remove();
            worksheet.Save();
            return $"已清除 {toRemove.Count} 条条件格式。";
        }
        catch (Exception ex) { return $"[错误] 清除失败: {ex.Message}"; }
    }

    [ToolFunction("excel_hyperlink_set")]
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
            var worksheet = RequireWorksheet(wsPart);
            var sheetData = worksheet.GetFirstChild<SheetData>()!;
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
            var hyperlinks = worksheet.Elements<Hyperlinks>().FirstOrDefault();
            if (hyperlinks == null) { hyperlinks = new Hyperlinks(); worksheet.AppendChild(hyperlinks); }
            hyperlinks.AppendChild(new Hyperlink { Reference = cellRef, Id = rel.Id });
            worksheet.Save();
            return $"已在 {cellRef} 设置超链接。";
        }
        catch (Exception ex) { return $"[错误] 设置失败: {ex.Message}"; }
    }

    [ToolFunction("excel_sheet_add")]
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
            var wbPart = RequireWorkbookPart(doc);
            var workbook = RequireWorkbook(wbPart);
            var sheets = RequireSheets(workbook);
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new Worksheet(new SheetData());
            uint newId = sheets.Elements<Sheet>().Any() ? sheets.Elements<Sheet>().Max(s => s.SheetId!.Value) + 1 : 1;
            sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = newId, Name = sheetName.Trim() });
            workbook.Save();
            return $"已添加工作表「{sheetName}」。";
        }
        catch (Exception ex) { return $"[错误] 添加失败: {ex.Message}"; }
    }

    [ToolFunction("excel_sheet_remove")]
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
            var wbPart = RequireWorkbookPart(doc);
            var workbook = RequireWorkbook(wbPart);
            var sheet = RequireSheets(workbook).Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName);
            if (sheet == null) return $"未找到工作表: {sheetName}";
            wbPart.DeletePart(wbPart.GetPartById(sheet.Id!.Value!));
            sheet.Remove();
            workbook.Save();
            return $"已删除工作表「{sheetName}」。";
        }
        catch (Exception ex) { return $"[错误] 删除失败: {ex.Message}"; }
    }

    [ToolFunction("excel_charts_list")]
    [Description("列出各工作表内嵌入式图表数量（插入→图表中的柱形/折线/饼图等均算；迷你图、纯形状/图片不算）。")]
    public string ExcelChartsList(
        [Description("Excel 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateExcelExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var wbPartCharts = RequireWorkbookPart(doc);
            var workbookCharts = RequireWorkbook(wbPartCharts);
            var sb = new StringBuilder();
            foreach (var sheet in RequireSheets(workbookCharts).Elements<Sheet>())
            {
                var wsPart = (WorksheetPart)wbPartCharts.GetPartById(sheet.Id!.Value!);
                // Excel 嵌入式图多在 DrawingsPart → ChartPart；直接挂在 WorksheetPart 上的较少见，两处都统计。
                var nCharts = wsPart.GetPartsOfType<ChartPart>().Count();
                foreach (var drawing in wsPart.GetPartsOfType<DrawingsPart>())
                    nCharts += drawing.GetPartsOfType<ChartPart>().Count();
                if (nCharts > 0)
                    sb.AppendLine($"工作表「{sheet.Name?.Value}」: {nCharts} 个图表");
            }
            var result = sb.ToString().TrimEnd();
            return string.IsNullOrEmpty(result) ? "工作簿中无图表。" : result;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    // ── Helpers ──

    private const string ExcelWorkbookStructErr = "[错误] Excel 工作簿结构不完整或已损坏。";

    private static WorkbookPart RequireWorkbookPart(SpreadsheetDocument doc) =>
        doc.WorkbookPart ?? throw new Exception(ExcelWorkbookStructErr);

    private static Workbook RequireWorkbook(WorkbookPart wbPart) =>
        wbPart.Workbook ?? throw new Exception(ExcelWorkbookStructErr);

    private static Sheets RequireSheets(Workbook workbook) =>
        workbook.Sheets ?? throw new Exception("[错误] 工作簿无工作表清单。");

    /// <summary>
    /// OOXML 要求 workbook 子元素严格按序；<c>definedNames</c> 必须在 <c>calcPr</c> 等节点之前。
    /// 使用 <see cref="OpenXmlCompositeElement.AppendChild"/> 会把 <c>definedNames</c> 放到末尾，若文件中已有 <c>calcPr</c> 会破坏结构，Excel 报「文件已损坏」。
    /// </summary>
    private static DefinedNames InsertDefinedNamesAfterOptionalPredecessors(Workbook workbook)
    {
        var definedNames = new DefinedNames();
        foreach (var child in workbook.ChildElements)
        {
            if (IsWorkbookChildAfterDefinedNamesSlot(child.LocalName))
            {
                workbook.InsertBefore(definedNames, child);
                return definedNames;
            }
        }

        var sheets = RequireSheets(workbook);
        OpenXmlElement anchor = workbook.ExternalReferences
            ?? (OpenXmlElement?)workbook.FunctionGroups
            ?? sheets;
        workbook.InsertAfter(definedNames, anchor);
        return definedNames;
    }

    private static bool IsWorkbookChildAfterDefinedNamesSlot(string localName) => localName switch
    {
        "calcPr" or "oleSize" or "customWorkbookViews" or "pivotCaches"
            or "smtpMailMergePr" or "smartTagPr" or "smartTagTypes" or "webPublishing"
            or "fileRecoveryPr" or "webPublishObjects" or "extLst" => true,
        _ => false,
    };

    private static Worksheet RequireWorksheet(WorksheetPart wsPart) =>
        wsPart.Worksheet ?? throw new Exception("[错误] 工作表部件缺少内容。");

    private static (WorksheetPart wsPart, Sheet sheet) GetWorksheetPart(SpreadsheetDocument doc, string sheetName)
    {
        var wbPart = RequireWorkbookPart(doc);
        var workbook = RequireWorkbook(wbPart);
        var sheets = RequireSheets(workbook);
        var sheet = string.IsNullOrEmpty(sheetName)
            ? sheets.Elements<Sheet>().First()
            : sheets.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName)
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
