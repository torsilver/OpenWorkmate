using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Plugins;

public sealed class WordPlugin
{
    private readonly ILogger<WordPlugin>? _logger;

    public WordPlugin(ILogger<WordPlugin>? logger = null) => _logger = logger;

    /// <summary>展开环境变量（如 %USERNAME%、%USERPROFILE%），并将“下载目录”相对路径解析为真实 Downloads 路径。</summary>
    private static string ResolveFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath;
        filePath = Environment.ExpandEnvironmentVariables(filePath);
        if (Path.IsPathRooted(filePath)) return filePath;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return filePath;
        var downloads = Path.Combine(userProfile, "Downloads");
        return Path.Combine(downloads, filePath.TrimStart('\\', '/'));
    }

    [KernelFunction("read_word_content")]
    [Description("读取 Word 文档(.docx)的文本内容。可以选择读取全部内容或指定段落范围。")]
    public string ReadContent(
        [Description("Word 文件的完整路径（支持 %USERNAME%、%USERPROFILE%；相对路径会解析到用户下载目录）")] string filePath,
        [Description("起始段落编号（从 1 开始），0 表示从头开始")] int startParagraph = 0,
        [Description("读取的最大段落数，0 表示读取全部")] int maxParagraphs = 0)
    {
        filePath = ResolveFilePath(filePath);
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var body = doc.MainDocumentPart!.Document.Body!;
            var paragraphs = body.Elements<Paragraph>().ToList();

            if (paragraphs.Count == 0)
                return "文档为空。";

            int start = startParagraph > 0 ? startParagraph - 1 : 0;
            int count = maxParagraphs > 0 ? maxParagraphs : paragraphs.Count - start;
            start = Math.Min(start, paragraphs.Count - 1);
            count = Math.Min(count, paragraphs.Count - start);

            var sb = new StringBuilder();
            sb.AppendLine($"文档共 {paragraphs.Count} 个段落，显示第 {start + 1}~{start + count} 段：\n");

            for (int i = start; i < start + count; i++)
            {
                var text = paragraphs[i].InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"[段落{i + 1}] {text}");
            }

            var result = sb.ToString().TrimEnd();
            if (result.Length > 8000)
                result = result[..8000] + "\n...(内容已截断)";

            return result;
        }
        catch (Exception ex)
        {
            return $"[错误] 读取失败: {ex.Message}";
        }
    }

    [KernelFunction("read_word_tables")]
    [Description("读取 Word 文档中所有表格的内容，以制表符分隔的文本格式返回。")]
    public string ReadTables(
        [Description("Word 文件的完整路径（支持 %USERNAME%、%USERPROFILE%；相对路径会解析到用户下载目录）")] string filePath)
    {
        filePath = ResolveFilePath(filePath);
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var body = doc.MainDocumentPart!.Document.Body!;
            var tables = body.Elements<Table>().ToList();

            if (tables.Count == 0)
                return "文档中没有表格。";

            var sb = new StringBuilder();
            for (int t = 0; t < tables.Count; t++)
            {
                sb.AppendLine($"--- 表格 {t + 1} ---");
                var rows = tables[t].Elements<TableRow>().ToList();
                foreach (var row in rows)
                {
                    var cells = row.Elements<TableCell>()
                        .Select(c => c.InnerText.Trim());
                    sb.AppendLine(string.Join('\t', cells));
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"[错误] 读取表格失败: {ex.Message}";
        }
    }

    [KernelFunction("write_word_document")]
    [Description("创建一个新的 Word 文档(.docx)，写入标题和多个段落。如果文件已存在会被覆盖。保存到下载目录时请使用 %USERPROFILE%\\Downloads\\文件名.docx 或 文件名.docx（相对路径会保存到用户下载目录）。")]
    public string WriteDocument(
        [Description("Word 文件的完整路径（支持 %USERNAME%、%USERPROFILE%；相对路径会保存到用户下载目录）")] string filePath,
        [Description("文档标题")] string title,
        [Description("段落内容列表，用 | 分隔多个段落，例如 '第一段内容|第二段内容|第三段内容'")] string paragraphs)
    {
        filePath = ResolveFilePath(filePath);
        _logger?.LogInformation("[Word] write_word_document path={Path}", filePath);
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            var titlePara = new Paragraph(
                new ParagraphProperties(
                    new ParagraphStyleId { Val = "Heading1" }),
                new Run(
                    new RunProperties(
                        new Bold(),
                        new FontSize { Val = "36" }),
                    new Text(title)));
            body.Append(titlePara);

            var parts = paragraphs.Split('|', StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                body.Append(new Paragraph(new Run(new Text(part))));
            }

            mainPart.Document.Save();
            var result = $"成功创建文档: {filePath}（标题: {title}，{parts.Length} 个段落）";
            _logger?.LogInformation("[Word] write_word_document success: {Path}", filePath);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Word] write_word_document failed path={Path}", filePath);
            return $"[错误] 创建文档失败: {ex.Message}";
        }
    }

    [KernelFunction("find_and_replace_in_word")]
    [Description("在 Word 文档中查找并替换文本。")]
    public string FindAndReplace(
        [Description("Word 文件的完整路径（支持 %USERNAME%、%USERPROFILE%；相对路径会解析到用户下载目录）")] string filePath,
        [Description("要查找的文本")] string searchText,
        [Description("替换为的文本")] string replaceText)
    {
        filePath = ResolveFilePath(filePath);
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            var body = doc.MainDocumentPart!.Document.Body!;
            int count = 0;

            foreach (var text in body.Descendants<Text>())
            {
                if (text.Text.Contains(searchText))
                {
                    text.Text = text.Text.Replace(searchText, replaceText);
                    count++;
                }
            }

            doc.MainDocumentPart.Document.Save();
            return count > 0
                ? $"替换完成，共替换了 {count} 处「{searchText}」→「{replaceText}」"
                : $"未找到「{searchText}」";
        }
        catch (Exception ex)
        {
            return $"[错误] 替换失败: {ex.Message}";
        }
    }

    [KernelFunction("format_word_paragraphs")]
    [Description("对 Word 文档中指定范围的段落设置对齐、样式、段前段后间距。段落号从 1 开始；endParagraph 为 0 表示到文档末尾。alignment: left|center|right|justify。styleId 如 Heading1、Heading2、Normal、Title。spacingBefore/After 单位为磅（如 6 表示 6 磅）。")]
    public string FormatParagraphs(
        [Description("Word 文件的完整路径（支持 %USERNAME%、%USERPROFILE%；相对路径会解析到用户下载目录）")] string filePath,
        [Description("起始段落号（从 1 开始）")] int startParagraph,
        [Description("结束段落号（与 start 相同则只处理一段）；0 表示到文档末尾")] int endParagraph = 0,
        [Description("对齐方式：left、center、right、justify，留空则不修改")] string? alignment = null,
        [Description("段落样式 ID，如 Heading1、Heading2、Normal、Title，留空则不修改")] string? styleId = null,
        [Description("段前间距（磅），0 或不传则不修改")] int spacingBefore = 0,
        [Description("段后间距（磅），0 或不传则不修改")] int spacingAfter = 0)
    {
        filePath = ResolveFilePath(filePath);
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            var body = doc.MainDocumentPart!.Document.Body!;
            var paragraphs = body.Elements<Paragraph>().ToList();
            if (paragraphs.Count == 0)
                return "文档中没有段落。";

            int start = Math.Max(1, startParagraph) - 1;
            int end = endParagraph <= 0 ? paragraphs.Count : Math.Min(endParagraph, paragraphs.Count);
            if (start >= paragraphs.Count || end < start)
                return $"段落范围无效（文档共 {paragraphs.Count} 段）。";

            var justValue = alignment?.Trim().ToLowerInvariant() switch
            {
                "center" => JustificationValues.Center,
                "right" => JustificationValues.Right,
                "justify" => JustificationValues.Both,
                "left" or _ => JustificationValues.Left
            };
            bool setAlign = !string.IsNullOrEmpty(alignment);
            bool setStyle = !string.IsNullOrWhiteSpace(styleId);
            bool setSpacing = spacingBefore > 0 || spacingAfter > 0;

            int count = 0;
            for (int i = start; i < end; i++)
            {
                var para = paragraphs[i];
                var pPr = para.Elements<ParagraphProperties>().FirstOrDefault();
                if (pPr == null)
                {
                    pPr = new ParagraphProperties();
                    para.InsertAt(pPr, 0);
                }

                if (setAlign)
                {
                    pPr.Justification = new Justification { Val = justValue };
                }
                if (setStyle)
                {
                    pPr.ParagraphStyleId = new ParagraphStyleId { Val = styleId!.Trim() };
                }
                if (setSpacing)
                {
                    var spacing = pPr.Elements<SpacingBetweenLines>().FirstOrDefault();
                    if (spacing == null)
                    {
                        spacing = new SpacingBetweenLines();
                        pPr.Append(spacing);
                    }
                    if (spacingBefore > 0)
                        spacing.Before = (spacingBefore * 20).ToString(); // 磅 -> twips
                    if (spacingAfter > 0)
                        spacing.After = (spacingAfter * 20).ToString();
                }
                count++;
            }

            doc.MainDocumentPart.Document.Save();
            return $"已格式化 {count} 个段落（{start + 1}～{start + count}）。";
        }
        catch (Exception ex)
        {
            return $"[错误] 格式化段落失败: {ex.Message}";
        }
    }

    [KernelFunction("format_word_text")]
    [Description("在 Word 文档中查找包含指定文字的所有 Run，并设置字体、字号、加粗、斜体、颜色。全部匹配到的 Run 都会应用格式。字号单位为磅；颜色为 6 位十六进制如 FF0000（红），不含 #。")]
    public string FormatText(
        [Description("Word 文件的完整路径（支持 %USERNAME%、%USERPROFILE%；相对路径会解析到用户下载目录）")] string filePath,
        [Description("要格式化的文字（包含该文字的 Run 会整段应用格式）")] string searchText,
        [Description("是否加粗，不传则不修改")] bool? bold = null,
        [Description("是否斜体，不传则不修改")] bool? italic = null,
        [Description("字号（磅），如 12，0 或不传则不修改")] int fontSize = 0,
        [Description("字体名称，如 宋体、Arial，留空则不修改")] string? fontName = null,
        [Description("字体颜色 6 位十六进制，如 FF0000 表示红色，留空则不修改")] string? colorHex = null)
    {
        if (string.IsNullOrEmpty(searchText))
            return "[错误] searchText 不能为空。";

        filePath = ResolveFilePath(filePath);
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            var body = doc.MainDocumentPart!.Document.Body!;
            var runs = body.Descendants<Run>().Where(r => r.InnerText.Contains(searchText)).ToList();
            if (runs.Count == 0)
                return $"未找到包含「{searchText}」的文字。";

            bool setBold = bold.HasValue;
            bool setItalic = italic.HasValue;
            bool setFontSize = fontSize > 0;
            bool setFontName = !string.IsNullOrWhiteSpace(fontName);
            bool setColor = !string.IsNullOrWhiteSpace(colorHex) && colorHex.Trim().Length >= 6;

            foreach (var run in runs)
            {
                var rPr = run.Elements<RunProperties>().FirstOrDefault();
                if (rPr == null)
                {
                    rPr = new RunProperties();
                    run.InsertAt(rPr, 0);
                }

                if (setBold)
                {
                    if (bold!.Value)
                        rPr.Bold = new Bold();
                    else
                        rPr.Bold?.Remove();
                }
                if (setItalic)
                {
                    if (italic!.Value)
                        rPr.Italic = new Italic();
                    else
                        rPr.Italic?.Remove();
                }
                if (setFontSize)
                    rPr.FontSize = new FontSize { Val = (fontSize * 2).ToString() }; // 磅 -> 半磅
                if (setFontName)
                    rPr.RunFonts = new RunFonts { Ascii = fontName!.Trim() };
                if (setColor)
                {
                    var hex = colorHex!.Trim();
                    if (hex.StartsWith("#"))
                        hex = hex[1..];
                    if (hex.Length >= 6)
                        rPr.Color = new Color { Val = hex.Length > 6 ? hex[..6] : hex };
                }
            }

            doc.MainDocumentPart.Document.Save();
            return $"已对 {runs.Count} 处包含「{searchText}」的文字应用格式。";
        }
        catch (Exception ex)
        {
            return $"[错误] 格式化文字失败: {ex.Message}";
        }
    }
}
