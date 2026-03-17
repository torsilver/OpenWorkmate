using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
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

    [KernelFunction("get_attachment_path")]
    [Description("Returns the local file path for a user attachment reference (e.g. attachment:xxx). Use this when you need to pass the file to another tool (e.g. OCR) that accepts a path. Returns empty or error message if the reference is invalid or expired.")]
    public string GetAttachmentPath(
        [Description("The attachment reference from the user message, e.g. attachment:abc123")] string attachmentRef)
    {
        var path = _attachmentCache.GetPath(attachmentRef);
        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("[File] get_attachment_path ref not found or expired: {Ref}", attachmentRef);
            return "失败：附件引用无效或已过期（约 30 分钟有效），请让用户重新发送图片。";
        }
        return path;
    }

    [KernelFunction("get_file_size")]
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

    [KernelFunction("save_screenshot_to_downloads")]
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
}
