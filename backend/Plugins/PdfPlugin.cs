using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenWorkmate.Server;
using OpenWorkmate.Server.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using PigPdfDocument = UglyToad.PdfPig.PdfDocument;
using SharpPdfDocument = PdfSharp.Pdf.PdfDocument;

namespace OpenWorkmate.Server.Plugins;

/// <summary>内置 PDF 读写：读用 PdfPig，写与合并用 PDFsharp。</summary>
[OpenWorkmatePluginId("Pdf")]
public sealed class PdfPlugin
{
    private readonly ILogger<PdfPlugin> _logger;

    private const string TruncationSuffix = "\n\n[已截断：超出 maxChars 限制。可缩小页码范围或增大 maxChars（上限见工具说明）。]";
    private const double MarginPt = 40;
    private const double LineLeadingFactor = 1.15;

    public PdfPlugin(ILogger<PdfPlugin> logger)
    {
        _logger = logger;
    }

    [ToolFunction("get_pdf_text")]
    [Description(
        "Extract text from a PDF at a local path (e.g. from get_attachment_path). Scanned PDFs may return little or no text; use OCR on rendered images if needed. Optional firstPage/lastPage are 1-based inclusive. maxChars defaults when omitted or non-positive; hard-capped. Returns text or a Chinese error starting with 失败：.")]
    public string GetPdfText(
        [Description("Full local path to the .pdf file")] string filePath,
        [Description("Optional first page (1-based). Omit for page 1.")] int? firstPage = null,
        [Description("Optional last page (1-based). Omit for last page.")] int? lastPage = null,
        [Description("Maximum characters to return; omit or ≤0 uses default (200000), capped at 2000000")] int? maxChars = null)
    {
        if (!TryValidateExistingPdfPath(filePath, out var fullPath, out var err))
            return err!;

        var limit = PdfToolNormalize.NormalizeMaxChars(maxChars);
        try
        {
            using var document = PigPdfDocument.Open(fullPath);
            var pageCount = document.NumberOfPages;
            if (!PdfToolNormalize.TryNormalizePageRange(pageCount, firstPage, lastPage, out var fromP, out var toP, out var rangeErr))
                return rangeErr!;

            var sb = new StringBuilder();
            var truncated = false;
            for (var p = fromP; p <= toP; p++)
            {
                if (sb.Length >= limit)
                {
                    truncated = true;
                    break;
                }

                var page = document.GetPage(p);
                var pageText = ContentOrderTextExtractor.GetText(page);
                var header = $"\n--- Page {p} ---\n";

                if (sb.Length + header.Length + pageText.Length <= limit)
                {
                    sb.Append(header);
                    sb.Append(pageText);
                }
                else
                {
                    sb.Append(header);
                    var room = limit - sb.Length;
                    if (room > 0)
                        sb.Append(pageText.AsSpan(0, Math.Min(room, pageText.Length)));
                    truncated = true;
                    break;
                }
            }

            if (sb.Length == 0 && !truncated)
                return "（未从指定页范围抽取到可见文本；可能是扫描件或仅含图片，可尝试 OCR。）";

            if (truncated)
                sb.Append(TruncationSuffix);
            return sb.ToString().TrimStart();
        }
        catch (PdfDocumentEncryptedException)
        {
            _logger.LogWarning("[Pdf] get_pdf_text encrypted without password: {Path}", fullPath);
            return "失败：PDF 已加密且未提供密码，无法抽取文本。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pdf] get_pdf_text failed: {Path}", fullPath);
            return "失败：读取或解析 PDF 时出错（" + ex.Message + "）。";
        }
    }

    [ToolFunction("get_pdf_info")]
    [Description(
        "Get PDF metadata: page count, whether encrypted, title/author if present. Path from get_attachment_path or local .pdf. Returns a short summary or 失败：.")]
    public string GetPdfInfo(
        [Description("Full local path to the .pdf file")] string filePath)
    {
        if (!TryValidateExistingPdfPath(filePath, out var fullPath, out var err))
            return err!;

        try
        {
            using var document = PigPdfDocument.Open(fullPath);
            var info = document.Information;
            var title = string.IsNullOrWhiteSpace(info.Title) ? "（无）" : info.Title;
            var author = string.IsNullOrWhiteSpace(info.Author) ? "（无）" : info.Author;
            var enc = document.IsEncrypted ? "是" : "否";
            return $"页数：{document.NumberOfPages}\n加密：{enc}\n标题：{title}\n作者：{author}";
        }
        catch (PdfDocumentEncryptedException)
        {
            _logger.LogWarning("[Pdf] get_pdf_info encrypted without password: {Path}", fullPath);
            return "失败：PDF 已加密且未提供密码，无法读取信息。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pdf] get_pdf_info failed: {Path}", fullPath);
            return "失败：读取或解析 PDF 时出错（" + ex.Message + "）。";
        }
    }

    [ToolFunction("pdf_document_create")]
    [Description(
        "Create a new PDF with plain text using PDFsharp. outputPath must end with .pdf (not .md/.txt). If overwrite is false (default) and outputPath already exists, fails. Use form feed \\f in bodyText to start a new page. Latin and common Windows fonts work best; complex scripts may need host fonts. Returns success message or 失败：.")]
    public string PdfDocumentCreate(
        [Description("Full path for the new file; must use .pdf extension (e.g. report.pdf), not .md or .txt")] string outputPath,
        [Description("Body text; use \\f between segments to force a new page")] string bodyText,
        [Description("If true, replace existing output file")] bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return "失败：请提供输出路径。";
        if (bodyText == null)
            return "失败：正文不能为 null。";

        var trimmed = outputPath.Trim();
        var beforeNorm = trimmed;
        trimmed = OpenXmlHelpers.NormalizePdfOutputPath(trimmed);
        if (!string.Equals(trimmed, beforeNorm, StringComparison.OrdinalIgnoreCase))
            _logger.LogInformation("[Pdf] pdf_document_create normalized output path from {Before} to {After}", beforeNorm, trimmed);

        var outFull = Path.GetFullPath(trimmed);
        if (!string.Equals(Path.GetExtension(outFull), ".pdf", StringComparison.OrdinalIgnoreCase))
            return "失败：输出路径须为 .pdf 扩展名。";

        try
        {
            var dir = Path.GetDirectoryName(outFull);
            if (string.IsNullOrEmpty(dir))
                return "失败：无法解析输出目录。";
            Directory.CreateDirectory(dir);

            if (File.Exists(outFull) && !overwrite)
                return "失败：目标文件已存在。若需覆盖请将 overwrite 设为 true。";

            var pages = bodyText.Split('\f', StringSplitOptions.None);
            var document = new SharpPdfDocument();
            document.Info.Title = Path.GetFileNameWithoutExtension(outFull);

            foreach (var pageText in pages)
            {
                var page = document.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Verdana", 11, XFontStyleEx.Regular);
                var top = MarginPt;
                var left = MarginPt;
                var right = page.Width.Point - MarginPt;
                var bottom = page.Height.Point - MarginPt;
                DrawTextBlockSinglePage(gfx, font, pageText ?? "", left, top, right, bottom);
            }

            document.Save(outFull);
            return "已写入：" + outFull;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pdf] pdf_document_create failed: {Path}", outputPath);
            return "失败：写入 PDF 时出错（" + ex.Message + "）。";
        }
    }

    [ToolFunction("pdf_merge")]
    [Description(
        "Merge multiple existing PDFs into one using PDFsharp, in the order given. inputPdfPaths: one absolute path per line (or separate with ;). outputPath must end with .pdf (not .md/.txt). If overwrite is false and output exists, fails. Returns success or 失败：.")]
    public string PdfMerge(
        [Description("Full path for merged output; must use .pdf extension")] string outputPath,
        [Description("Input PDF paths, one per line or separated by ;")] string inputPdfPaths,
        [Description("If true, replace existing output file")] bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return "失败：请提供输出路径。";
        if (string.IsNullOrWhiteSpace(inputPdfPaths))
            return "失败：请提供至少一个输入 PDF 路径。";

        var trimmedOut = outputPath.Trim();
        var beforeNormOut = trimmedOut;
        trimmedOut = OpenXmlHelpers.NormalizePdfOutputPath(trimmedOut);
        if (!string.Equals(trimmedOut, beforeNormOut, StringComparison.OrdinalIgnoreCase))
            _logger.LogInformation("[Pdf] pdf_merge normalized output path from {Before} to {After}", beforeNormOut, trimmedOut);

        var outFull = Path.GetFullPath(trimmedOut);
        if (!string.Equals(Path.GetExtension(outFull), ".pdf", StringComparison.OrdinalIgnoreCase))
            return "失败：输出路径须为 .pdf 扩展名。";

        var rawPaths = inputPdfPaths
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (rawPaths.Count < 2)
            return "失败：合并至少需要两个有效的输入 PDF 路径（每行一个或用分号分隔）。";

        var inputs = new List<string>();
        foreach (var p in rawPaths)
        {
            if (!TryValidateExistingPdfPath(p, out var fp, out var e))
                return e!;
            inputs.Add(fp);
        }

        try
        {
            var dir = Path.GetDirectoryName(outFull);
            if (string.IsNullOrEmpty(dir))
                return "失败：无法解析输出目录。";
            Directory.CreateDirectory(dir);

            if (File.Exists(outFull) && !overwrite)
                return "失败：目标文件已存在。若需覆盖请将 overwrite 设为 true。";

            using var output = new SharpPdfDocument();
            foreach (var path in inputs)
            {
                using var opened = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                var count = opened.PageCount;
                for (var i = 0; i < count; i++)
                    output.AddPage(opened.Pages[i]);
            }

            output.Save(outFull);
            return "已合并写入：" + outFull + $"（共 {output.PageCount} 页）";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pdf] pdf_merge failed -> {Path}", outFull);
            return "失败：合并 PDF 时出错（" + ex.Message + "）。";
        }
    }

    private static bool TryValidateExistingPdfPath(string filePath, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "失败：请提供 PDF 文件路径。";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(filePath.Trim());
        }
        catch (Exception)
        {
            error = "失败：路径无效。";
            return false;
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            error = "失败：文件须为 .pdf。";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "失败：文件不存在或不可访问（" + fullPath + "）。";
            return false;
        }

        return true;
    }

    /// <summary>在单页内绘制多行文本；超出页高则截断并注明（避免跨页时错误释放 <see cref="XGraphics"/>）。</summary>
    private static void DrawTextBlockSinglePage(XGraphics gfx, XFont font, string text, double left, double top, double right, double bottom)
    {
        var maxWidth = right - left;
        var lineHeight = font.GetHeight() * LineLeadingFactor;
        var y = top;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var truncated = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine ?? "";
            foreach (var wrapped in WrapLineToWidth(gfx, font, line, maxWidth))
            {
                if (y + lineHeight > bottom)
                {
                    truncated = true;
                    break;
                }

                gfx.DrawString(wrapped, font, XBrushes.Black, left, y);
                y += lineHeight;
            }

            if (truncated)
                break;
        }

        if (truncated && y + lineHeight <= bottom)
            gfx.DrawString("[本页剩余内容过长已省略；可用 \\f 分页写入。]", font, XBrushes.Black, left, y);
    }

    private static IEnumerable<string> WrapLineToWidth(XGraphics gfx, XFont font, string line, double maxWidth)
    {
        if (string.IsNullOrEmpty(line))
        {
            yield return "";
            yield break;
        }

        var words = line.Split(' ', StringSplitOptions.None);
        var current = new StringBuilder();
        foreach (var w in words)
        {
            if (w.Length == 0)
                continue;

            var trial = current.Length == 0 ? w : current + " " + w;
            var tw = gfx.MeasureString(trial, font).Width;
            if (tw <= maxWidth)
            {
                if (current.Length > 0)
                    current.Append(' ');
                current.Append(w);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (gfx.MeasureString(w, font).Width <= maxWidth)
                current.Append(w);
            else
            {
                foreach (var part in BreakLongToken(gfx, font, w, maxWidth))
                    yield return part;
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static IEnumerable<string> BreakLongToken(XGraphics gfx, XFont font, string token, double maxWidth)
    {
        if (string.IsNullOrEmpty(token))
            yield break;
        var i = 0;
        while (i < token.Length)
        {
            var lo = i + 1;
            var hi = token.Length;
            var best = i + 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var slice = token.Substring(i, mid - i);
                if (gfx.MeasureString(slice, font).Width <= maxWidth)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                    hi = mid - 1;
            }

            if (best <= i)
                best = i + 1;
            yield return token.Substring(i, best - i);
            i = best;
        }
    }
}
