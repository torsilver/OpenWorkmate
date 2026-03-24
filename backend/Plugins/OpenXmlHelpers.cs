using System.Diagnostics.CodeAnalysis;

namespace OfficeCopilot.Server.Plugins;

/// <summary>Excel/Word 插件共用的路径解析与扩展名校验，Phase 0 共享层。</summary>
public static class OpenXmlHelpers
{
    /// <summary>展开环境变量，相对路径解析到用户下载目录。</summary>
    public static string ResolvePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath ?? "";
        filePath = Environment.ExpandEnvironmentVariables(filePath);
        if (Path.IsPathRooted(filePath)) return filePath;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return filePath;
        var downloads = Path.Combine(userProfile, "Downloads");
        return Path.Combine(downloads, filePath.TrimStart('\\', '/'));
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
}
