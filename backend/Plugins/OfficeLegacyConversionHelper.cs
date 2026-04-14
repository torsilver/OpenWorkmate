using System.Diagnostics.CodeAnalysis;

namespace OfficeCopilot.Server.Plugins;

/// <summary>旧版 Office 二进制扩展名与输出路径规则（无 COM，便于单测）。</summary>
public enum OfficeLegacyKind
{
    Word,
    Excel,
    PowerPoint
}

public static class OfficeLegacyConversionHelper
{
    /// <summary>根据输入路径扩展名判断是否支持转换。</summary>
    public static bool TryGetLegacyKind(string filePath, out OfficeLegacyKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".doc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dot", StringComparison.OrdinalIgnoreCase))
        {
            kind = OfficeLegacyKind.Word;
            return true;
        }

        if (ext.Equals(".xls", StringComparison.OrdinalIgnoreCase))
        {
            kind = OfficeLegacyKind.Excel;
            return true;
        }

        if (ext.Equals(".ppt", StringComparison.OrdinalIgnoreCase))
        {
            kind = OfficeLegacyKind.PowerPoint;
            return true;
        }

        kind = default;
        return false;
    }

    public static string GetExpectedOpenXmlExtension(OfficeLegacyKind kind) =>
        kind switch
        {
            OfficeLegacyKind.Word => ".docx",
            OfficeLegacyKind.Excel => ".xlsx",
            OfficeLegacyKind.PowerPoint => ".pptx",
            _ => ".docx"
        };

    /// <summary>校验用户指定的输出路径扩展名与种类一致。</summary>
    public static bool ValidateOutputExtension(string outputFullPath, OfficeLegacyKind kind, [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        var expected = GetExpectedOpenXmlExtension(kind);
        var ext = Path.GetExtension(outputFullPath);
        if (string.IsNullOrEmpty(ext))
        {
            errorMessage = $"[错误] outputPath 缺少扩展名，应为 {expected}。";
            return false;
        }

        if (!ext.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"[错误] outputPath 扩展名应为 {expected}（当前为 {ext}）。";
            return false;
        }

        return true;
    }

    /// <summary>缺省输出：同目录 <c>原名_converted.docx</c> 等；若已存在则返回错误（避免静默覆盖）。</summary>
    public static bool TryBuildDefaultOutputPath(string inputFullPath, OfficeLegacyKind kind, [NotNullWhen(true)] out string? outputFullPath, [NotNullWhen(false)] out string? errorMessage)
    {
        outputFullPath = null;
        errorMessage = null;
        var dir = Path.GetDirectoryName(inputFullPath);
        var stem = Path.GetFileNameWithoutExtension(inputFullPath);
        if (string.IsNullOrEmpty(stem))
        {
            errorMessage = "[错误] 无法从输入路径解析文件名。";
            return false;
        }

        var ext = GetExpectedOpenXmlExtension(kind);
        var candidate = string.IsNullOrEmpty(dir)
            ? stem + "_converted" + ext
            : Path.Combine(dir, stem + "_converted" + ext);
        if (File.Exists(candidate))
        {
            errorMessage = "[错误] 默认输出文件已存在: " + candidate + "。请删除该文件或在 outputPath 中指定其他路径。";
            return false;
        }

        outputFullPath = candidate;
        return true;
    }

    public static bool InputFileExists(string fullPath, [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        if (!File.Exists(fullPath))
        {
            errorMessage = "[错误] 输入文件不存在或不可访问: " + fullPath;
            return false;
        }

        try
        {
            var fi = new FileInfo(fullPath);
            if (fi.Attributes.HasFlag(FileAttributes.Directory))
            {
                errorMessage = "[错误] inputPath 指向目录而非文件。";
                return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = "[错误] 无法访问输入路径: " + ex.Message;
            return false;
        }

        return true;
    }
}
