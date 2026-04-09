using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

public sealed class FilePlugin
{
    private readonly ScreenshotCacheService _screenshotCache;
    private readonly AttachmentCacheService _attachmentCache;
    private readonly ILogger<FilePlugin> _logger;

    public FilePlugin(ScreenshotCacheService screenshotCache, AttachmentCacheService attachmentCache, ILogger<FilePlugin> logger)
    {
        _screenshotCache = screenshotCache;
        _attachmentCache = attachmentCache;
        _logger = logger;
    }

    [ToolFunction("get_attachment_path")]
    [Description("Returns the local file path for a user attachment reference (e.g. attachment:xxx). Use this when you need to pass the file to another tool (e.g. OCR) that accepts a path. Returns empty or error message if the reference is invalid or expired.")]
    public string GetAttachmentPath(
        [Description("The attachment reference from the user message, e.g. attachment:abc123")] string attachmentRef)
    {
        if (!_attachmentCache.TryResolvePath(attachmentRef, out var path, out var resolveFailure))
        {
            switch (resolveFailure)
            {
                case AttachmentRefResolveFailure.InvalidFormat:
                    _logger.LogDebug("[File] get_attachment_path invalid ref format: {Ref}", attachmentRef);
                    return "失败：附件引用格式无效。请使用用户消息中的完整引用（例如 attachment: 后跟 32 位十六进制 id），勿只传裸 id。";
                case AttachmentRefResolveFailure.NotFound:
                    _logger.LogWarning("[File] get_attachment_path ref not in cache: {Ref}", attachmentRef);
                    return "失败：未找到该附件，可能本会话未上传或引用错误。请让用户重新发送图片。";
                case AttachmentRefResolveFailure.Expired:
                    _logger.LogWarning("[File] get_attachment_path ref expired (TTL): {Ref}", attachmentRef);
                    return "失败：附件引用已过期（约 30 分钟有效），请让用户重新发送图片。";
                default:
                    _logger.LogWarning("[File] get_attachment_path unresolved: {Ref}", attachmentRef);
                    return "失败：无法解析附件引用，请让用户重新发送图片。";
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("[File] get_attachment_path no local path (memory-only cache): {Ref}", attachmentRef);
            return "失败：附件在服务端无本地文件路径，无法交给 OCR 等工具。请让用户重新发送图片。";
        }

        return path;
    }

    [ToolFunction("get_file_size")]
    [Description("Get the size of a file in bytes and human-readable form. Use when you need to decide whether to include file content in context or which tool to use (e.g. OCR, STT, or read in chunks). Path can be from get_attachment_path or a local file path. Returns size or an error message if the file does not exist or is not accessible.")]
    public string GetFileSize(
        [Description("Full local path to the file (e.g. from get_attachment_path or C:\\temp\\doc.pdf)")] string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "失败：请提供文件路径。";
        var path = filePath.Trim();
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("[File] get_file_size file not found: {Path}", fullPath);
                return "失败：文件不存在或路径不可访问（" + fullPath + "）。";
            }
            var fi = new FileInfo(fullPath);
            if (fi.Attributes.HasFlag(FileAttributes.Directory))
                return "失败：路径指向的是目录而非文件，请指定具体文件路径。";
            var bytes = fi.Length;
            var readable = FormatByteSize(bytes);
            return $"{bytes} ({readable})";
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "[File] get_file_size access denied: {Path}", path);
            return "失败：无权限访问该文件（" + ex.Message + "）。";
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            _logger.LogWarning(ex, "[File] get_file_size invalid path: {Path}", path);
            return "失败：路径无效或不可访问（" + ex.Message + "）。";
        }
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        var v = (double)bytes;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v} {units[i]}" : $"{v:F2} {units[i]}";
    }

    [ToolFunction("save_screenshot_to_downloads")]
    [Description("Saves a screenshot (previously captured via capture_full_page) to the user's Downloads folder. Pass the screenshot reference returned by capture_full_page as the first argument; optional second argument is the filename without extension.")]
    public string SaveScreenshotToDownloads(
        [Description("The screenshot reference from capture_full_page, e.g. screenshot:abc123")] string screenshotRef,
        [Description("Optional filename without extension; if empty, uses screenshot_yyyyMMdd_HHmmss")] string? filename = null)
    {
        var bytes = _screenshotCache.TryTake(screenshotRef);
        if (bytes == null || bytes.Length == 0)
        {
            _logger.LogWarning("[File] save_screenshot_to_downloads ref not found or expired: {Ref}", screenshotRef);
            return "失败：截图引用无效或已过期，请先重新执行整页截图。";
        }

        var downloadsDir = GetDownloadsFolder();
        if (string.IsNullOrEmpty(downloadsDir))
        {
            _logger.LogWarning("[File] Could not resolve Downloads folder");
            return "失败：无法解析用户下载文件夹路径。";
        }

        var baseName = string.IsNullOrWhiteSpace(filename)
            ? "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
            : SanitizeFileName(filename.Trim());
        var path = Path.Combine(downloadsDir, baseName + ".png");

        try
        {
            if (!Directory.Exists(downloadsDir))
                Directory.CreateDirectory(downloadsDir);
            File.WriteAllBytes(path, bytes);
            _logger.LogInformation("[File] Saved screenshot to {Path}", path);
            return "成功：截图已保存到 " + path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[File] Failed to write screenshot to {Path}", path);
            return "失败：写入文件时出错（" + ex.Message + "）。";
        }
    }

    private static string? GetDownloadsFolder()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return null;
        var downloads = Path.Combine(userProfile, "Downloads");
        if (Directory.Exists(downloads)) return downloads;
        return downloads;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var sanitized = Regex.Replace(name, "[" + Regex.Escape(invalid) + "]", "_");
        return string.IsNullOrEmpty(sanitized) ? "screenshot" : sanitized;
    }

    [ToolFunction("text_file_read")]
    [Description(
        "Read plain text from a local file. Allowed extensions: .txt, .md, .markdown, .json, .csv. Path resolution matches Word/Excel (relative names go under the current user's Downloads). Default encoding utf-8; use gbk for many legacy Chinese text files. maxChars defaults to 200000, capped at 2000000; content is truncated with a suffix if longer. Raw file size must not exceed 8 MiB. Returns file content or a message starting with 失败：.")]
    public async Task<string> TextFileReadAsync(
        [Description("File path; use .txt/.md/.json/.csv etc. Relative paths resolve to the user's Downloads folder.")] string filePath,
        [Description("Optional max characters to return; omit or ≤0 uses 200000, capped at 2000000")] int? maxChars = null,
        [Description("Optional: utf-8 (default), utf-8-bom, or gbk")] string? encoding = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "失败：请提供文件路径。";

        var resolved = OpenXmlHelpers.ResolvePath(filePath.Trim());
        if (!OpenXmlHelpers.ValidateTextFileExtension(resolved, out var extErr))
            return "失败：" + TrimErrorPrefix(extErr);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            _logger.LogWarning(ex, "[File] text_file_read invalid path: {Path}", filePath);
            return "失败：路径无效或不可访问（" + ex.Message + "）。";
        }

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("[File] text_file_read file not found: {Path}", fullPath);
            return "失败：文件不存在或路径不可访问（" + fullPath + "）。";
        }

        try
        {
            var fi = new FileInfo(fullPath);
            if (fi.Attributes.HasFlag(FileAttributes.Directory))
                return "失败：路径指向的是目录而非文件，请指定具体文件路径。";
            if (fi.Length > TextFileToolNormalize.MaxBytesToRead)
                return $"失败：文件过大（{fi.Length} 字节），超过单次读取上限 {TextFileToolNormalize.MaxBytesToRead} 字节（8 MiB）。请改用更小的文件或分段处理。";
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "[File] text_file_read access denied: {Path}", fullPath);
            return "失败：无权限访问该路径（" + ex.Message + "）。";
        }

        if (!TryGetTextEncoding(encoding, forWrite: false, out var enc, out var encErr))
            return "失败：" + encErr;

        string text;
        try
        {
            text = await File.ReadAllTextAsync(fullPath, enc, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "[File] text_file_read read denied: {Path}", fullPath);
            return "失败：无权限读取该文件（" + ex.Message + "）。";
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[File] text_file_read IO error: {Path}", fullPath);
            return "失败：读取文件时出错（" + ex.Message + "）。";
        }

        var limit = TextFileToolNormalize.NormalizeMaxChars(maxChars);
        var outText = TextFileToolNormalize.ApplyMaxCharLimit(text, limit, out _);
        return outText;
    }

    [ToolFunction("text_file_write")]
    [Description(
        "Write or append plain text to a local file. Same allowed extensions and path rules as text_file_read. Creates parent directories if needed. append=false overwrites existing files. Encoding: utf-8 (default), utf-8-bom (UTF-8 with BOM), or gbk. Returns success message or 失败：.")]
    public async Task<string> TextFileWriteAsync(
        [Description("Target file path (.txt/.md/.json/.csv/.markdown); relative paths resolve to Downloads")] string filePath,
        [Description("Full text to write")] string content,
        [Description("If true, append to existing file; if false, overwrite or create")] bool append = false,
        [Description("Optional: utf-8 (default), utf-8-bom, or gbk")] string? encoding = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "失败：请提供文件路径。";

        var resolved = OpenXmlHelpers.ResolvePath(filePath.Trim());
        if (!OpenXmlHelpers.ValidateTextFileExtension(resolved, out var extErr))
            return "失败：" + TrimErrorPrefix(extErr);

        if (!TryGetTextEncoding(encoding, forWrite: true, out var enc, out var encErr))
            return "失败：" + encErr;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            _logger.LogWarning(ex, "[File] text_file_write invalid path: {Path}", filePath);
            return "失败：路径无效或不可访问（" + ex.Message + "）。";
        }

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (append)
                await File.AppendAllTextAsync(fullPath, content ?? "", enc, cancellationToken).ConfigureAwait(false);
            else
                await File.WriteAllTextAsync(fullPath, content ?? "", enc, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "[File] text_file_write access denied: {Path}", fullPath);
            return "失败：无权限写入该路径（" + ex.Message + "）。";
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[File] text_file_write IO error: {Path}", fullPath);
            return "失败：写入文件时出错（" + ex.Message + "）。";
        }

        _logger.LogInformation("[File] text_file_write {Mode} {Path}", append ? "append" : "write", fullPath);
        return "成功：已" + (append ? "追加" : "写入") + "文件 " + fullPath;
    }

    private static string TrimErrorPrefix(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        return message.StartsWith("[错误] ", StringComparison.Ordinal)
            ? message[5..]
            : message.TrimStart();
    }

    private static bool TryGetTextEncoding(string? encodingName, bool forWrite, out Encoding encoding, out string? error)
    {
        encoding = Encoding.UTF8;
        error = null;
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var n = encodingName?.Trim();
        if (string.IsNullOrEmpty(n) || n.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || n.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return true;
        }

        if (n.Equals("utf-8-bom", StringComparison.OrdinalIgnoreCase) || n.Equals("utf8-bom", StringComparison.OrdinalIgnoreCase))
        {
            encoding = forWrite
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                : Encoding.UTF8;
            return true;
        }

        if (n.Equals("gbk", StringComparison.OrdinalIgnoreCase) || n.Equals("gb2312", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                encoding = Encoding.GetEncoding("gbk");
                return true;
            }
            catch (Exception ex)
            {
                error = "无法使用 GBK 编码（" + ex.Message + "）。";
                return false;
            }
        }

        error = "不支持的 encoding，请使用 utf-8、utf-8-bom 或 gbk。";
        return false;
    }
}
