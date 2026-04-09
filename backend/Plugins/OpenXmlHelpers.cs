using System.Diagnostics.CodeAnalysis;

namespace OfficeCopilot.Server.Plugins;

/// <summary>Excel/Word/PPT 等插件共用的路径解析、扩展名校验，以及「创建输出」前的常见误用扩展名纠正。</summary>
public static class OpenXmlHelpers
{
    /// <summary>展开环境变量，相对路径解析到用户下载目录。</summary>
    /// <remarks>
    /// 模型常把「下载文件夹」写成 <c>C:\Users\Public\Downloads</c> 或 <c>%PUBLIC%\Downloads</c>（Public 为共享配置档）。
    /// 此类路径重定向到当前登录用户的 <c>UserProfile\Downloads</c>，避免文件落到 Public 下。
    /// </remarks>
    public static string ResolvePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath ?? "";
        filePath = Environment.ExpandEnvironmentVariables(filePath.Trim());
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile))
            return Path.IsPathRooted(filePath) ? filePath : filePath;

        var userDownloads = Path.Combine(userProfile, "Downloads");

        if (Path.IsPathRooted(filePath))
        {
            var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
            if (!string.IsNullOrWhiteSpace(publicRoot))
            {
                var publicDownloads = Path.GetFullPath(Path.Combine(publicRoot, "Downloads"));
                var full = Path.GetFullPath(filePath);
                if (full.StartsWith(publicDownloads, StringComparison.OrdinalIgnoreCase) &&
                    (full.Length == publicDownloads.Length ||
                     full[publicDownloads.Length] == Path.DirectorySeparatorChar ||
                     full[publicDownloads.Length] == Path.AltDirectorySeparatorChar))
                {
                    var rel = full.Length > publicDownloads.Length
                        ? full.Substring(publicDownloads.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : "";
                    return string.IsNullOrEmpty(rel) ? userDownloads : Path.Combine(userDownloads, rel);
                }
            }

            return filePath;
        }

        return Path.Combine(userDownloads, filePath.TrimStart('\\', '/'));
    }

    /// <summary>仅允许 .xlsx / .xlsm；.xls 返回明确错误。</summary>
    public static bool ValidateExcelExtension(string filePath, [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        var ext = Path.GetExtension(filePath ?? "");
        if (string.IsNullOrEmpty(ext)) { errorMessage = "[错误] 文件无扩展名，需要 .xlsx 或 .xlsm。"; return false; }
        if (ext.Equals(".xls", StringComparison.OrdinalIgnoreCase))
        { errorMessage = "[错误] 暂不支持 .xls 格式，请将文件另存为 .xlsx 或 .xlsm 后重试。"; return false; }
        if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            return true;
        errorMessage = "[错误] 仅支持 .xlsx 或 .xlsm 文件。"; return false;
    }

    /// <summary>
    /// 供 <c>word_document_create</c> 在校验前调用：将模型常见的错误扩展名改为 <c>.docx</c>，无扩展名则补 <c>.docx</c>。
    /// 不改动已是 <c>.docx</c>/<c>.docm</c> 的路径，其它未知扩展名保持原样交由 <see cref="ValidateWordExtension"/> 报错。
    /// </summary>
    public static string NormalizeWordCreateOutputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath;
        filePath = filePath.Trim();
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return filePath + ".docx";
        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".docm", StringComparison.OrdinalIgnoreCase))
            return filePath;
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".doc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            return filePath[..^ext.Length] + ".docx";
        return filePath;
    }

    /// <summary>
    /// 供 <c>excel_range_write</c> 等「会新建工作簿」的路径在校验前调用：无扩展名或常见误用改为 <c>.xlsx</c>；已是 <c>.xlsm</c> 则保留。
    /// </summary>
    public static string NormalizeExcelCreateOutputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath;
        filePath = filePath.Trim();
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return filePath + ".xlsx";
        if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            return filePath;
        if (ext.Equals(".xls", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return filePath[..^ext.Length] + ".xlsx";
        return filePath;
    }

    /// <summary>
    /// 供 <c>ppt_document_create</c> 在校验前调用：无扩展名或常见误用改为 <c>.pptx</c>；已是 <c>.pptm</c> 则保留。
    /// </summary>
    public static string NormalizePptCreateOutputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath;
        filePath = filePath.Trim();
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return filePath + ".pptx";
        if (ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pptm", StringComparison.OrdinalIgnoreCase))
            return filePath;
        if (ext.Equals(".ppt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return filePath[..^ext.Length] + ".pptx";
        return filePath;
    }

    /// <summary>
    /// 供 <c>pdf_document_create</c> / <c>pdf_merge</c> 输出路径：无扩展名或 .md/.txt 等改为 <c>.pdf</c>。
    /// </summary>
    public static string NormalizePdfOutputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath;
        filePath = filePath.Trim();
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return filePath + ".pdf";
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return filePath;
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return filePath[..^ext.Length] + ".pdf";
        return filePath;
    }

    /// <summary>仅允许 .docx / .docm；.doc 返回明确错误。</summary>
    public static bool ValidateWordExtension(string filePath, [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        var ext = Path.GetExtension(filePath ?? "");
        if (string.IsNullOrEmpty(ext)) { errorMessage = "[错误] 文件无扩展名，需要 .docx 或 .docm。"; return false; }
        if (ext.Equals(".doc", StringComparison.OrdinalIgnoreCase))
        { errorMessage = "[错误] 暂不支持 .doc 格式，请将文件另存为 .docx 或 .docm 后重试。"; return false; }
        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".docm", StringComparison.OrdinalIgnoreCase))
            return true;
        errorMessage = "[错误] 仅支持 .docx 或 .docm 文件。"; return false;
    }

    /// <summary>仅允许 .pptx / .pptm；.ppt 返回明确错误。</summary>
    public static bool ValidatePptExtension(string filePath, [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        var ext = Path.GetExtension(filePath ?? "");
        if (string.IsNullOrEmpty(ext)) { errorMessage = "[错误] 文件无扩展名，需要 .pptx 或 .pptm。"; return false; }
        if (ext.Equals(".ppt", StringComparison.OrdinalIgnoreCase))
        { errorMessage = "[错误] 暂不支持 .ppt 格式，请将文件另存为 .pptx 或 .pptm 后重试。"; return false; }
        if (ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pptm", StringComparison.OrdinalIgnoreCase))
            return true;
        errorMessage = "[错误] 仅支持 .pptx 或 .pptm 文件。"; return false;
    }

    /// <summary>仅允许 .txt / .md / .markdown / .json / .csv（供 <see cref="FilePlugin"/> 文本读写）。</summary>
    public static bool ValidateTextFileExtension(string? filePath, [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            errorMessage = "[错误] 文件路径为空。";
            return false;
        }

        var ext = Path.GetExtension(filePath.Trim());
        if (string.IsNullOrEmpty(ext))
        {
            errorMessage = "[错误] 文件无扩展名，需要 .txt、.md、.json、.csv 等允许的文本类扩展名。";
            return false;
        }

        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return true;

        errorMessage = "[错误] 不支持的扩展名（仅允许 .txt、.md、.markdown、.json、.csv）。";
        return false;
    }
}
