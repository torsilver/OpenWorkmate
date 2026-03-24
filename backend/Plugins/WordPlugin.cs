using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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

    private const string WordStructureErrMsg = "[错误] Word 文档结构不完整或已损坏（缺少主文档或正文）。";

    private static bool TryGetMainPart(WordprocessingDocument doc, [NotNullWhen(true)] out MainDocumentPart? main, out string errorMessage)
    {
        main = doc.MainDocumentPart;
        if (main == null) { errorMessage = WordStructureErrMsg; return false; }
        errorMessage = "";
        return true;
    }

    private static bool TryGetMainAndBody(WordprocessingDocument doc, [NotNullWhen(true)] out MainDocumentPart? main, [NotNullWhen(true)] out Body? body, out string errorMessage)
    {
        main = doc.MainDocumentPart;
        body = null;
        if (main == null) { errorMessage = WordStructureErrMsg; return false; }
        if (main.Document?.Body is not { } b) { errorMessage = WordStructureErrMsg; return false; }
        body = b;
        errorMessage = "";
        return true;
    }

    [KernelFunction("word_body_read")]
    [Description("读取 Word 文档正文：段落（可选含表格）。支持段落范围与长度截断。")]
    public string WordBodyRead(
        [Description("Word 文件完整路径，支持环境变量与相对路径")] string filePath,
        [Description("起始段落号，从 1 开始，0 表示从头")] int startParagraph = 0,
        [Description("最多读取段落数，0 表示全部")] int maxParagraphs = 0,
        [Description("是否同时输出表格内容")] bool includeTables = true)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainAndBody(doc, out _, out var body, out var structErr)) return structErr;
            var blocks = body.ChildElements.ToList();
            var paragraphs = blocks.OfType<Paragraph>().ToList();
            int start = startParagraph > 0 ? startParagraph - 1 : 0;
            int count = maxParagraphs > 0 ? maxParagraphs : Math.Max(0, paragraphs.Count - start);
            start = Math.Min(start, paragraphs.Count);
            count = Math.Min(count, Math.Max(0, paragraphs.Count - start));

            var sb = new StringBuilder();
            sb.AppendLine($"文档共 {paragraphs.Count} 个段落，显示第 {start + 1}～{start + count} 段：\n");
            for (int i = start; i < start + count && i < paragraphs.Count; i++)
            {
                var text = paragraphs[i].InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"[段落{i + 1}] {text}");
            }
            if (includeTables)
            {
                var tables = body.Elements<Table>().ToList();
                if (tables.Count > 0)
                {
                    sb.AppendLine("\n--- 表格 ---");
                    for (int t = 0; t < tables.Count; t++)
                    {
                        sb.AppendLine($"表格 {t + 1}:");
                        foreach (var row in tables[t].Elements<TableRow>())
                            sb.AppendLine(string.Join('\t', row.Elements<TableCell>().Select(c => c.InnerText.Trim())));
                    }
                }
            }
            var result = sb.ToString().TrimEnd();
            if (result.Length > 8000) result = result[..8000] + "\n...(已截断)";
            return result;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_tables_list")]
    [Description("列出文档中所有表格的索引与简要信息。")]
    public string WordTablesList(
        [Description("Word 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainAndBody(doc, out _, out var body, out var structErr)) return structErr;
            var tables = body.Elements<Table>().ToList();
            if (tables.Count == 0) return "文档中无表格。";
            var sb = new StringBuilder();
            for (int i = 0; i < tables.Count; i++)
            {
                var rows = tables[i].Elements<TableRow>().Count();
                var cols = tables[i].Elements<TableRow>().FirstOrDefault()?.Elements<TableCell>().Count() ?? 0;
                sb.AppendLine($"表格 {i + 1}: 约 {rows} 行 x {cols} 列");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_tables_read")]
    [Description("读取一个或全部表格内容，制表符分隔。tableIndex 为 0 时读全部。")]
    public string WordTablesRead(
        [Description("Word 文件完整路径")] string filePath,
        [Description("表格索引，从 1 开始；0 表示全部")] int tableIndex = 0)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainAndBody(doc, out _, out var body, out var structErr)) return structErr;
            var tables = body.Elements<Table>().ToList();
            if (tables.Count == 0) return "文档中无表格。";
            var sb = new StringBuilder();
            var indices = tableIndex <= 0 ? Enumerable.Range(0, tables.Count) : new[] { tableIndex - 1 };
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= tables.Count) continue;
                sb.AppendLine($"--- 表格 {idx + 1} ---");
                foreach (var row in tables[idx].Elements<TableRow>())
                    sb.AppendLine(string.Join('\t', row.Elements<TableCell>().Select(c => c.InnerText.Trim())));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_document_create")]
    [Description("创建新 Word 文档并写入标题与段落；文件已存在则覆盖。paragraphs 中可用 Markdown 约定：以 # / ## / ### 开头的行自动变为标题，以 - 或 * 开头的行自动变为列表项，其余为正文段落。")]
    public string WordDocumentCreate(
        [Description("Word 文件完整路径")] string filePath,
        [Description("文档标题")] string title,
        [Description("段落内容，用 | 分隔多个段落。支持 Markdown：# 标题、- 列表")] string paragraphs)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        _logger?.LogInformation("[Word] word_document_create path={Path}", filePath);
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var isDocm = filePath.EndsWith(".docm", StringComparison.OrdinalIgnoreCase);
            var docType = isDocm ? WordprocessingDocumentType.MacroEnabledDocument : WordprocessingDocumentType.Document;
            using var doc = WordprocessingDocument.Create(filePath, docType);
            var mainPart = doc.AddMainDocumentPart();
            AddDefaultStyles(mainPart);
            mainPart.Document = new Document(new Body());
            if (mainPart.Document?.Body is not { } body)
                return "[错误] 创建文档后未能初始化正文。";

            // Title paragraph with Heading1 style
            body.Append(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new Text(title))));

            // Parse each paragraph with Markdown conventions
            foreach (var part in paragraphs.Split('|', StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrEmpty(part)) continue;
                body.Append(ParseMarkdownParagraph(part));
            }

            // A4 page setup with standard margins
            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838, Orient = PageOrientationValues.Portrait },
                new PageMargin { Top = 1440, Right = 1800U, Bottom = 1440, Left = 1800U, Header = 720U, Footer = 720U }));

            mainPart.Document.Save();
            return $"已创建文档: {filePath}（标题: {title}）";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Word] word_document_create failed path={Path}", filePath);
            return $"[错误] 创建失败: {ex.Message}";
        }
    }

    private static void AddDefaultStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        // Default run properties for the document
        var docDefaults = new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "微软雅黑", ComplexScript = "Calibri" },
                new FontSize { Val = "21" }, // 10.5pt
                new FontSizeComplexScript { Val = "21" },
                new Languages { Val = "en-US", EastAsia = "zh-CN" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { After = "160", Line = "360", LineRule = LineSpacingRuleValues.Auto }))); // 段后 8pt, 1.5 倍行距
        styles.Append(docDefaults);

        // Normal style
        styles.Append(new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new SpacingBetweenLines { After = "160", Line = "360", LineRule = LineSpacingRuleValues.Auto }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        // Heading 1
        styles.Append(new Style(
            new StyleName { Val = "heading 1" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", EastAsia = "微软雅黑" },
                new Bold(),
                new FontSize { Val = "44" }, // 22pt
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "1F3864" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "360", After = "120" },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading1" });

        // Heading 2
        styles.Append(new Style(
            new StyleName { Val = "heading 2" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", EastAsia = "微软雅黑" },
                new Bold(),
                new FontSize { Val = "32" }, // 16pt
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "2E75B6" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "240", After = "80" },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading2" });

        // Heading 3
        styles.Append(new Style(
            new StyleName { Val = "heading 3" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", EastAsia = "微软雅黑" },
                new Bold(),
                new FontSize { Val = "28" }, // 14pt
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "404040" }),
            new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "200", After = "80" },
                new KeepNext()))
        { Type = StyleValues.Paragraph, StyleId = "Heading3" });

        // List Paragraph style
        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" },
            new BasedOn { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new Indentation { Left = "720" },
                new SpacingBetweenLines { After = "80" }))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });

        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    private static Paragraph ParseMarkdownParagraph(string text)
    {
        // Heading detection: # / ## / ###
        if (text.StartsWith("### ", StringComparison.Ordinal))
        {
            var content = text[4..].Trim();
            return new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading3" }),
                new Run(new Text(content)));
        }
        if (text.StartsWith("## ", StringComparison.Ordinal))
        {
            var content = text[3..].Trim();
            return new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }),
                new Run(new Text(content)));
        }
        if (text.StartsWith("# ", StringComparison.Ordinal))
        {
            var content = text[2..].Trim();
            return new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new Text(content)));
        }

        // Bullet list: - or *
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
        {
            var content = text[2..].Trim();
            var pPr = new ParagraphProperties(
                new ParagraphStyleId { Val = "ListParagraph" },
                new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = 1 }));
            return new Paragraph(pPr, new Run(new Text(content)));
        }

        // Normal paragraph with 2-char first line indent
        var normalPPr = new ParagraphProperties(
            new ParagraphStyleId { Val = "Normal" },
            new Indentation { FirstLine = "420" }); // ~2 chars at 10.5pt
        return new Paragraph(normalPPr, new Run(new Text(text)));
    }

    [KernelFunction("word_find_replace")]
    [Description("在文档中查找并替换文本。")]
    public string WordFindReplace(
        [Description("Word 文件完整路径")] string filePath,
        [Description("要查找的文本")] string searchText,
        [Description("替换为的文本")] string replaceText)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var body, out var structErr)) return structErr;
            int count = 0;
            foreach (var text in body.Descendants<Text>())
            {
                if (text.Text?.Contains(searchText) == true)
                {
                    text.Text = text.Text.Replace(searchText, replaceText);
                    count++;
                }
            }
            main.Document!.Save();
            return count > 0 ? $"已替换 {count} 处「{searchText}」→「{replaceText}」" : $"未找到「{searchText}」";
        }
        catch (Exception ex) { return $"[错误] 替换失败: {ex.Message}"; }
    }

    [KernelFunction("word_paragraphs_format")]
    [Description("对指定段落设置对齐、样式、段前段后间距。alignment: left|center|right|justify。")]
    public string WordParagraphsFormat(
        [Description("Word 文件完整路径")] string filePath,
        [Description("起始段落号，从 1 开始")] int startParagraph,
        [Description("结束段落号，0 表示到文档末尾")] int endParagraph = 0,
        [Description("对齐：left、center、right、justify")] string? alignment = null,
        [Description("段落样式 ID，如 Heading1、Normal")] string? styleId = null,
        [Description("段前间距（磅）")] int spacingBefore = 0,
        [Description("段后间距（磅）")] int spacingAfter = 0)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var body, out var structErr)) return structErr;
            var paragraphs = body.Elements<Paragraph>().ToList();
            if (paragraphs.Count == 0) return "文档中无段落。";
            int start = Math.Max(1, startParagraph) - 1;
            int end = endParagraph <= 0 ? paragraphs.Count : Math.Min(endParagraph, paragraphs.Count);
            if (start >= paragraphs.Count || end < start) return $"段落范围无效（共 {paragraphs.Count} 段）。";
            var justValue = alignment?.Trim().ToLowerInvariant() switch
            {
                "center" => JustificationValues.Center,
                "right" => JustificationValues.Right,
                "justify" => JustificationValues.Both,
                _ => JustificationValues.Left
            };
            bool setAlign = !string.IsNullOrEmpty(alignment);
            bool setStyle = !string.IsNullOrWhiteSpace(styleId);
            bool setSpacing = spacingBefore > 0 || spacingAfter > 0;
            for (int i = start; i < end; i++)
            {
                var para = paragraphs[i];
                var pPr = para.Elements<ParagraphProperties>().FirstOrDefault() ?? (ParagraphProperties)para.InsertAt(new ParagraphProperties(), 0);
                if (setAlign) pPr.Justification = new Justification { Val = justValue };
                if (setStyle) pPr.ParagraphStyleId = new ParagraphStyleId { Val = styleId!.Trim() };
                if (setSpacing)
                {
                    var spacing = pPr.Elements<SpacingBetweenLines>().FirstOrDefault();
                    if (spacing == null) { spacing = new SpacingBetweenLines(); pPr.Append(spacing); }
                    if (spacingBefore > 0) spacing.Before = (spacingBefore * 20).ToString();
                    if (spacingAfter > 0) spacing.After = (spacingAfter * 20).ToString();
                }
            }
            main.Document!.Save();
            return $"已格式化第 {start + 1}～{end} 段，共 {end - start} 个段落。";
        }
        catch (Exception ex) { return $"[错误] 格式化失败: {ex.Message}"; }
    }

    [KernelFunction("word_text_format")]
    [Description("对包含指定文字的所有 Run 设置字体、字号、加粗、斜体、颜色。")]
    public string WordTextFormat(
        [Description("Word 文件完整路径")] string filePath,
        [Description("要格式化的文字（包含该文字的 Run 会应用格式）")] string searchText,
        [Description("是否加粗")] bool? bold = null,
        [Description("是否斜体")] bool? italic = null,
        [Description("字号（磅）")] int fontSize = 0,
        [Description("字体名称")] string? fontName = null,
        [Description("颜色 6 位十六进制，如 FF0000")] string? colorHex = null)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrEmpty(searchText)) return "[错误] searchText 不能为空。";
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var body, out var structErr)) return structErr;
            var runs = body.Descendants<Run>().Where(r => r.InnerText.Contains(searchText)).ToList();
            if (runs.Count == 0) return $"未找到包含「{searchText}」的文字。";
            foreach (var run in runs)
            {
                var rPr = run.Elements<RunProperties>().FirstOrDefault() ?? (RunProperties)run.InsertAt(new RunProperties(), 0);
                if (bold.HasValue) { if (bold.Value) rPr.Bold = new Bold(); else rPr.Bold?.Remove(); }
                if (italic.HasValue) { if (italic.Value) rPr.Italic = new Italic(); else rPr.Italic?.Remove(); }
                if (fontSize > 0) rPr.FontSize = new FontSize { Val = (fontSize * 2).ToString() };
                if (!string.IsNullOrWhiteSpace(fontName)) rPr.RunFonts = new RunFonts { Ascii = fontName!.Trim(), HighAnsi = fontName.Trim(), EastAsia = fontName.Trim() };
                if (!string.IsNullOrWhiteSpace(colorHex) && colorHex!.Trim().Length >= 6)
                    rPr.Color = new DocumentFormat.OpenXml.Wordprocessing.Color { Val = colorHex.Trim().StartsWith("#") ? colorHex.Trim()[1..].Length > 6 ? colorHex.Trim()[1..7] : colorHex.Trim()[1..] : colorHex.Trim().Length > 6 ? colorHex.Trim()[..6] : colorHex.Trim() };
            }
            main.Document!.Save();
            return $"已对 {runs.Count} 处包含「{searchText}」的文字应用格式。";
        }
        catch (Exception ex) { return $"[错误] 格式化失败: {ex.Message}"; }
    }

    [KernelFunction("word_comments_list")]
    [Description("列出文档中所有批注的 Id、作者与摘要。")]
    public string WordCommentsList(
        [Description("Word 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var commentsPart = doc.MainDocumentPart!.WordprocessingCommentsPart;
            if (commentsPart?.Comments == null || !commentsPart.Comments.Elements<Comment>().Any())
                return "文档中无批注。";
            var sb = new StringBuilder();
            foreach (var c in commentsPart.Comments.Elements<Comment>())
            {
                var id = c.Id?.Value ?? "";
                var author = c.Author?.Value ?? "";
                var preview = c.InnerText?.Length > 50 ? c.InnerText[..50] + "..." : c.InnerText ?? "";
                sb.AppendLine($"Id={id}\tAuthor={author}\t{preview}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_comments_read")]
    [Description("读取所有批注内容，含被批注原文（若有）。")]
    public string WordCommentsRead(
        [Description("Word 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var commentsPart = doc.MainDocumentPart!.WordprocessingCommentsPart;
            if (commentsPart?.Comments == null || !commentsPart.Comments.Elements<Comment>().Any())
                return "文档中无批注。";
            if (!TryGetMainAndBody(doc, out _, out var body, out var structErr)) return structErr;
            var sb = new StringBuilder();
            foreach (var comment in commentsPart.Comments.Elements<Comment>())
            {
                var id = comment.Id?.Value ?? "";
                var author = comment.Author?.Value ?? "";
                var text = comment.InnerText ?? "";
                var refText = GetCommentedRangeText(body, id);
                sb.AppendLine($"[批注 Id={id} Author={author}]");
                if (!string.IsNullOrEmpty(refText)) sb.AppendLine($"  被批注原文: {refText}");
                sb.AppendLine($"  批注内容: {text}");
                sb.AppendLine();
            }
            var result = sb.ToString().TrimEnd();
            if (result.Length > 12000) result = result[..12000] + "\n...(已截断)";
            return result;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_comment_add")]
    [Description("在包含指定文字的首次出现处插入批注。")]
    public string WordCommentAdd(
        [Description("Word 文件完整路径")] string filePath,
        [Description("被批注的文字（首次匹配的 Run）")] string anchorText,
        [Description("批注内容")] string commentText,
        [Description("作者名")] string author = "User")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrEmpty(anchorText)) return "[错误] anchorText 不能为空。";
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var mainPart, out var body, out var structErr)) return structErr;
            var commentsPart = mainPart.WordprocessingCommentsPart ?? mainPart.AddNewPart<WordprocessingCommentsPart>();
            if (commentsPart.Comments == null) commentsPart.Comments = new Comments();

            uint maxId = 0;
            foreach (var c in commentsPart.Comments.Elements<Comment>())
                if (uint.TryParse(c.Id?.Value, out var id) && id > maxId) maxId = id;
            var newId = (maxId + 1).ToString();

            var comment = new Comment
            {
                Id = new StringValue(newId),
                Author = new StringValue(author),
                Date = new DateTimeValue(DateTime.UtcNow)
            };
            comment.AppendChild(new Paragraph(new Run(new Text(commentText))));
            commentsPart.Comments.AppendChild(comment);
            commentsPart.Comments.Save();

            Run? targetRun = null;
            Text? targetText = null;
            foreach (var run in body.Descendants<Run>())
            {
                foreach (var text in run.Elements<Text>())
                {
                    if (text.Text?.Contains(anchorText) == true)
                    {
                        targetRun = run; targetText = text; break;
                    }
                }
                if (targetRun != null) break;
            }
            if (targetRun == null || targetText == null)
                return "已添加批注内容，但未在正文中找到锚点文字，批注未关联到文档位置。";

            var commentRangeStart = new CommentRangeStart { Id = new StringValue(newId) };
            var commentRangeEnd = new CommentRangeEnd { Id = new StringValue(newId) };
            var commentRef = new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Baseline }), new CommentReference { Id = new StringValue(newId) });

            targetRun.InsertBefore(commentRangeStart, targetRun.FirstChild);
            targetRun.InsertAfter(commentRangeEnd, targetRun.LastChild);
            targetRun.Parent!.InsertAfter(commentRef, targetRun);
            mainPart.Document!.Save();
            return $"已添加批注(Id={newId})到「{anchorText}」处。";
        }
        catch (Exception ex) { return $"[错误] 添加批注失败: {ex.Message}"; }
    }

    [KernelFunction("word_comments_delete")]
    [Description("按批注 Id 删除批注，或删除全部。commentId 留空且 deleteAll 为 true 时删除全部。")]
    public string WordCommentsDelete(
        [Description("Word 文件完整路径")] string filePath,
        [Description("要删除的批注 Id，多个用逗号分隔")] string commentId = "",
        [Description("是否删除全部批注")] bool deleteAll = false)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainPart(doc, out var mainDoc, out var structErr0)) return structErr0;
            var commentsPart = mainDoc.WordprocessingCommentsPart;
            if (commentsPart?.Comments == null) return "文档中无批注。";
            var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (deleteAll)
                toRemove.UnionWith(commentsPart.Comments.Elements<Comment>().Select(c => c.Id?.Value ?? "").Where(s => !string.IsNullOrEmpty(s)));
            else if (!string.IsNullOrWhiteSpace(commentId))
                foreach (var id in commentId.Split(',', StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrEmpty(id)) toRemove.Add(id);

            if (toRemove.Count == 0) return "未指定要删除的批注。";
            if (mainDoc.Document?.Body is not { } body) return WordStructureErrMsg;
            foreach (var id in toRemove)
            {
                foreach (var el in body.Descendants<CommentRangeStart>().Where(x => x.Id?.Value == id).ToList())
                    el.Remove();
                foreach (var el in body.Descendants<CommentRangeEnd>().Where(x => x.Id?.Value == id).ToList())
                    el.Remove();
                foreach (var run in body.Descendants<Run>().ToList())
                {
                    var refEl = run.Elements<CommentReference>().FirstOrDefault(x => x.Id?.Value == id);
                    if (refEl != null) run.Remove();
                }
                var comment = commentsPart.Comments.Elements<Comment>().FirstOrDefault(c => c.Id?.Value == id);
                comment?.Remove();
            }
            commentsPart.Comments.Save();
            mainDoc.Document!.Save();
            return $"已删除 {toRemove.Count} 个批注。";
        }
        catch (Exception ex) { return $"[错误] 删除失败: {ex.Message}"; }
    }

    [KernelFunction("word_part_xml_read")]
    [Description("读取文档指定部件的原始 XML（document、comments、styles 等），maxChars 限制长度。")]
    public string WordPartXmlRead(
        [Description("Word 文件完整路径")] string filePath,
        [Description("部件类型：document、comments、styles")] string part = "document",
        [Description("最大字符数，0 不限制")] int maxChars = 0)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            string xml;
            switch (part.Trim().ToLowerInvariant())
            {
                case "document":
                    if (!TryGetMainPart(doc, out var mdPart, out var docErr)) return docErr;
                    if (mdPart.Document is not { } wordDoc) return WordStructureErrMsg;
                    xml = wordDoc.OuterXml;
                    break;
                case "comments":
                    var cp = doc.MainDocumentPart!.WordprocessingCommentsPart;
                    xml = cp?.Comments?.OuterXml ?? "<w:comments />";
                    break;
                case "styles":
                    var sp = doc.MainDocumentPart!.StyleDefinitionsPart;
                    xml = sp?.Styles?.OuterXml ?? "<w:styles />";
                    break;
                default:
                    return $"[错误] 不支持的 part: {part}，可选 document、comments、styles。";
            }
            if (maxChars > 0 && xml.Length > maxChars)
                xml = xml[..maxChars] + "\n...(已截断)";
            return xml;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_headers_footers_list")]
    [Description("按节列出文档中的页眉页脚（索引与类型）。")]
    public string WordHeadersFootersList(
        [Description("Word 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainPart(doc, out var main, out var structErr)) return structErr;
            var sb = new StringBuilder();
            int i = 0;
            foreach (var hp in main.HeaderParts) { i++; sb.AppendLine($"页眉 {i}: {hp.Uri}"); }
            i = 0;
            foreach (var fp in main.FooterParts) { i++; sb.AppendLine($"页脚 {i}: {fp.Uri}"); }
            var result = sb.ToString().TrimEnd();
            return string.IsNullOrEmpty(result) ? "文档中无页眉页脚。" : result;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_header_read")]
    [Description("读取指定索引的页眉文本。index 从 1 开始。")]
    public string WordHeaderRead(
        [Description("Word 文件完整路径")] string filePath,
        [Description("页眉索引，从 1 开始")] int index = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var parts = doc.MainDocumentPart!.HeaderParts.ToList();
            if (index < 1 || index > parts.Count) return $"页眉索引无效（共 {parts.Count} 个）。";
            var text = parts[index - 1].Header?.InnerText ?? "";
            return string.IsNullOrEmpty(text) ? "(空页眉)" : text;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_footer_read")]
    [Description("读取指定索引的页脚文本。index 从 1 开始。")]
    public string WordFooterRead(
        [Description("Word 文件完整路径")] string filePath,
        [Description("页脚索引，从 1 开始")] int index = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            var parts = doc.MainDocumentPart!.FooterParts.ToList();
            if (index < 1 || index > parts.Count) return $"页脚索引无效（共 {parts.Count} 个）。";
            var text = parts[index - 1].Footer?.InnerText ?? "";
            return string.IsNullOrEmpty(text) ? "(空页脚)" : text;
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_header_write")]
    [Description("写入或替换指定页眉的文本。若不存在则创建。")]
    public string WordHeaderWrite(
        [Description("Word 文件完整路径")] string filePath,
        [Description("页眉索引，从 1 开始；0 表示第一个或新建")] int index = 1,
        [Description("页眉文本内容")] string text = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var mainBody, out var structErr)) return structErr;
            HeaderPart? hp;
            if (index >= 1 && index <= main.HeaderParts.Count())
                hp = main.HeaderParts.ToList()[index - 1];
            else
            {
                hp = main.AddNewPart<HeaderPart>();
                hp.Header = new Header(new Paragraph(new Run(new Text(text))));
                var relId = main.GetIdOfPart(hp);
                foreach (var sect in mainBody.Descendants<SectionProperties>())
                {
                    var refH = sect.Elements<HeaderReference>().FirstOrDefault();
                    if (refH == null) { refH = new HeaderReference { Type = HeaderFooterValues.Default, Id = relId }; sect.InsertAt(refH, 0); }
                }
                main.Document!.Save();
                return "已添加新页眉。";
            }
            if (hp.Header == null) hp.Header = new Header();
            hp.Header.RemoveAllChildren<Paragraph>();
            hp.Header.AppendChild(new Paragraph(new Run(new Text(text))));
            hp.Header.Save();
            return $"已更新页眉 {index}。";
        }
        catch (Exception ex) { return $"[错误] 写入失败: {ex.Message}"; }
    }

    [KernelFunction("word_footer_write")]
    [Description("写入或替换指定页脚的文本。")]
    public string WordFooterWrite(
        [Description("Word 文件完整路径")] string filePath,
        [Description("页脚索引，从 1 开始；0 表示第一个或新建")] int index = 1,
        [Description("页脚文本内容")] string text = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var mainBody, out var structErr)) return structErr;
            FooterPart? fp;
            if (index >= 1 && index <= main.FooterParts.Count())
                fp = main.FooterParts.ToList()[index - 1];
            else
            {
                fp = main.AddNewPart<FooterPart>();
                fp.Footer = new Footer(new Paragraph(new Run(new Text(text))));
                var relId = main.GetIdOfPart(fp);
                foreach (var sect in mainBody.Descendants<SectionProperties>())
                {
                    var refF = sect.Elements<FooterReference>().FirstOrDefault();
                    if (refF == null) { refF = new FooterReference { Type = HeaderFooterValues.Default, Id = relId }; sect.InsertAt(refF, 0); }
                }
                main.Document!.Save();
                return "已添加新页脚。";
            }
            if (fp.Footer == null) fp.Footer = new Footer();
            fp.Footer.RemoveAllChildren<Paragraph>();
            fp.Footer.AppendChild(new Paragraph(new Run(new Text(text))));
            fp.Footer.Save();
            return $"已更新页脚 {index}。";
        }
        catch (Exception ex) { return $"[错误] 写入失败: {ex.Message}"; }
    }

    [KernelFunction("word_bookmarks_list")]
    [Description("列出文档中所有书签名称。")]
    public string WordBookmarksList(
        [Description("Word 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainAndBody(doc, out _, out var body, out var structErr)) return structErr;
            var starts = body.Descendants<BookmarkStart>().Select(b => b.Name?.Value ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            return starts.Count == 0 ? "文档中无书签。" : string.Join("\n", starts);
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_bookmark_read")]
    [Description("读取书签所标记范围的文本。")]
    public string WordBookmarkRead(
        [Description("Word 文件完整路径")] string filePath,
        [Description("书签名称")] string name = "")
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(name)) return "[错误] 请提供书签名称。";
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainAndBody(doc, out _, out var body, out var structErr)) return structErr;
            var start = body.Descendants<BookmarkStart>().FirstOrDefault(b => b.Name?.Value == name);
            if (start == null) return $"未找到书签: {name}";
            var bookmarkId = start.Id?.Value ?? "";
            var sb = new StringBuilder();
            var curr = start.NextSibling();
            while (curr != null)
            {
                if (curr is BookmarkEnd end && end.Id?.Value == bookmarkId) break;
                if (curr is Run r) sb.Append(r.InnerText);
                else if (curr is Paragraph p) sb.Append(p.InnerText);
                curr = curr.NextSibling();
            }
            return sb.ToString().Trim();
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_bookmark_insert")]
    [Description("在指定位置插入书签。paragraphIndex 从 1 开始，表示在该段末尾插入。")]
    public string WordBookmarkInsert(
        [Description("Word 文件完整路径")] string filePath,
        [Description("书签名称")] string name = "",
        [Description("段落索引，从 1 开始")] int paragraphIndex = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(name)) return "[错误] 请提供书签名称。";
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var body, out var structErr)) return structErr;
            var paragraphs = body.Elements<Paragraph>().ToList();
            if (paragraphIndex < 1 || paragraphIndex > paragraphs.Count) return $"段落索引无效（共 {paragraphs.Count} 段）。";
            var maxId = 0L;
            foreach (var b in body.Descendants<BookmarkStart>())
                if (long.TryParse(b.Id?.Value, out var id) && id > maxId) maxId = id;
            var newId = (maxId + 1).ToString();
            var para = paragraphs[paragraphIndex - 1];
            para.AppendChild(new BookmarkStart { Name = name, Id = newId });
            para.AppendChild(new BookmarkEnd { Id = newId });
            main.Document!.Save();
            return $"已在第 {paragraphIndex} 段插入书签「{name}」。";
        }
        catch (Exception ex) { return $"[错误] 插入失败: {ex.Message}"; }
    }

    [KernelFunction("word_images_list")]
    [Description("列出文档中图片部件数量与关系 Id。")]
    public string WordImagesList(
        [Description("Word 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainPart(doc, out var main, out var structErr)) return structErr;
            var parts = main.ImageParts?.Count() ?? 0;
            if (parts == 0) return "文档中无图片。";
            var ids = main.Parts.Where(p => p.OpenXmlPart is ImagePart).Select(p => p.RelationshipId).ToList();
            return $"共 {parts} 个图片部件。RelationshipIds: {string.Join(", ", ids)}";
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_image_insert")]
    [Description("在指定段落后插入图片。imagePath 为本地图片文件路径。")]
    public string WordImageInsert(
        [Description("Word 文件完整路径")] string filePath,
        [Description("要插入的图片文件路径")] string imagePath = "",
        [Description("段落索引，从 1 开始，在该段后插入")] int paragraphIndex = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return "[错误] 请提供存在的图片文件路径。";
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var body, out var structErr)) return structErr;
            var paragraphs = body.Elements<Paragraph>().ToList();
            if (paragraphIndex < 1 || paragraphIndex > paragraphs.Count) return $"段落索引无效（共 {paragraphs.Count} 段）。";
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            ImagePart imagePart = ext is ".jpg" or ".jpeg" ? main.AddImagePart(ImagePartType.Jpeg)
                : ext == ".gif" ? main.AddImagePart(ImagePartType.Gif)
                : ext == ".bmp" ? main.AddImagePart(ImagePartType.Bmp)
                : main.AddImagePart(ImagePartType.Png);
            using (var stream = File.OpenRead(imagePath))
                imagePart.FeedData(stream);
            var relId = main.GetIdOfPart(imagePart);
            var picUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
            var inline = new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 990000L, Cy = 792000L },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent(),
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1U, Name = "Picture 1" },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.NonVisualGraphicFrameDrawingProperties(
                    new DocumentFormat.OpenXml.Drawing.GraphicFrameLocks { NoChangeAspect = true }),
                new DocumentFormat.OpenXml.Drawing.Graphic(
                    new DocumentFormat.OpenXml.Drawing.GraphicData(
                        new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 0U, Name = "Image" },
                                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                            new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                                new DocumentFormat.OpenXml.Drawing.Blip { Embed = relId },
                                new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
                            new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
                                new DocumentFormat.OpenXml.Drawing.Transform2D(
                                    new DocumentFormat.OpenXml.Drawing.Offset { X = 0L, Y = 0L },
                                    new DocumentFormat.OpenXml.Drawing.Extents { Cx = 990000L, Cy = 792000L }),
                                new DocumentFormat.OpenXml.Drawing.PresetGeometry { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle })))
                    { Uri = picUri }));
            var newPara = new Paragraph(new Run(new Drawing(inline)));
            paragraphs[paragraphIndex - 1].InsertAfterSelf(newPara);
            main.Document!.Save();
            return $"已在第 {paragraphIndex} 段后插入图片。";
        }
        catch (Exception ex) { return $"[错误] 插入失败: {ex.Message}"; }
    }

    [KernelFunction("word_sections_list")]
    [Description("列出文档中的节（SectionProperties 数量）。")]
    public string WordSectionsList(
        [Description("Word 文件完整路径")] string filePath)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, false);
            if (!TryGetMainAndBody(doc, out _, out var body, out var structErr)) return structErr;
            var count = body.Descendants<SectionProperties>().Count();
            return count == 0 ? "文档中无显式节属性。" : $"共 {count} 个节。";
        }
        catch (Exception ex) { return $"[错误] 读取失败: {ex.Message}"; }
    }

    [KernelFunction("word_hyperlink_insert")]
    [Description("在指定段落插入超链接文本。")]
    public string WordHyperlinkInsert(
        [Description("Word 文件完整路径")] string filePath,
        [Description("显示文本")] string displayText = "",
        [Description("链接 URL")] string url = "",
        [Description("段落索引，从 1 开始，在该段末尾插入")] int paragraphIndex = 1)
    {
        filePath = OpenXmlHelpers.ResolvePath(filePath);
        if (!OpenXmlHelpers.ValidateWordExtension(filePath, out var extErr)) return extErr;
        if (string.IsNullOrWhiteSpace(url)) return "[错误] 请提供 url。";
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, true);
            if (!TryGetMainAndBody(doc, out var main, out var body, out var structErr)) return structErr;
            var paragraphs = body.Elements<Paragraph>().ToList();
            if (paragraphIndex < 1 || paragraphIndex > paragraphs.Count) return $"段落索引无效（共 {paragraphs.Count} 段）。";
            var rel = main.AddHyperlinkRelationship(new Uri(url, UriKind.Absolute), true);
            var hyperlink = new DocumentFormat.OpenXml.Wordprocessing.Hyperlink { Id = rel.Id };
            hyperlink.AppendChild(new Run(
                new RunProperties(new RunStyle { Val = "Hyperlink" }, new DocumentFormat.OpenXml.Wordprocessing.Color { ThemeColor = DocumentFormat.OpenXml.Wordprocessing.ThemeColorValues.Hyperlink }, new Underline { Val = DocumentFormat.OpenXml.Wordprocessing.UnderlineValues.Single }),
                new Text(string.IsNullOrEmpty(displayText) ? url : displayText)));
            paragraphs[paragraphIndex - 1].AppendChild(hyperlink);
            main.Document!.Save();
            return $"已在第 {paragraphIndex} 段插入超链接。";
        }
        catch (Exception ex) { return $"[错误] 插入失败: {ex.Message}"; }
    }

    private static string? GetCommentedRangeText(OpenXmlElement body, string commentId)
    {
        var startEl = body.Descendants<CommentRangeStart>().FirstOrDefault(x => x.Id?.Value == commentId);
        if (startEl == null) return null;
        var sb = new StringBuilder();
        var curr = startEl.NextSibling();
        while (curr != null)
        {
            if (curr is CommentRangeEnd end && end.Id?.Value == commentId) break;
            if (curr is Run r) sb.Append(r.InnerText);
            else if (curr is Paragraph p) sb.Append(p.InnerText);
            curr = curr.NextSibling();
        }
        return sb.ToString().Trim();
    }
}
