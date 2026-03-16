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
