using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OfficeCopilot.Server;

/// <summary>本机调试：解析 Serilog 滚动日志路径并安全读取尾部（仅允许 office-copilot-*.txt）。</summary>
public static class DebugLogHelper
{
    private static readonly Regex SafeLogName = new(
        @"^office-copilot-\d{8}(_\d+)?\.txt$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string LogsDirectory => Path.Combine(Directory.GetCurrentDirectory(), "logs");

    public static bool IsDebugLogLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        // TestServer / 部分 in-proc 请求无 RemoteIpAddress；仅用于本机调试接口，按本机处理。
        if (ip == null) return true;
        return IPAddress.IsLoopback(ip);
    }

    public static IReadOnlyList<string> ListLogFileNames()
    {
        var dir = LogsDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<string>();

        return Directory.GetFiles(dir, "office-copilot-*.txt")
            .Select(Path.GetFileName)
            .Where(n => n != null && SafeLogName.IsMatch(n))
            .Cast<string>()
            .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(dir, f)))
            .ToList();
    }

    public static bool IsAllowedLogFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Contains('\\') || name.Contains('/') || name.Contains("..")) return false;
        return SafeLogName.IsMatch(name);
    }

    /// <summary>读取指定文件或最新文件的尾部行（最多 maxLines）。</summary>
    public static (string? fileName, IReadOnlyList<string> lines, string? error) ReadTail(string? fileName, int maxLines)
    {
        if (maxLines < 1) maxLines = 1;
        if (maxLines > 20_000) maxLines = 20_000;

        var dir = LogsDirectory;
        if (!Directory.Exists(dir))
            return (null, Array.Empty<string>(), "logs 目录不存在（尚无日志文件）。");

        string path;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            var names = ListLogFileNames();
            if (names.Count == 0)
                return (null, Array.Empty<string>(), "未找到 office-copilot-*.txt 日志文件。");
            path = Path.Combine(dir, names[0]);
            fileName = names[0];
        }
        else
        {
            if (!IsAllowedLogFileName(fileName))
                return (null, Array.Empty<string>(), "非法的日志文件名。");
            path = Path.GetFullPath(Path.Combine(dir, fileName));
            var fullDir = Path.GetFullPath(dir);
            if (!path.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, fullDir, StringComparison.OrdinalIgnoreCase))
                return (null, Array.Empty<string>(), "路径越界。");
            if (!File.Exists(path))
                return (fileName, Array.Empty<string>(), "文件不存在。");
        }

        try
        {
            var lines = ReadLastLines(path, maxLines);
            return (fileName, lines, null);
        }
        catch (Exception ex)
        {
            return (fileName, Array.Empty<string>(), "读取日志失败：" + ex.Message);
        }
    }

    private static List<string> ReadLastLines(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return new List<string>();

        const long maxSimpleBytes = 16 * 1024 * 1024;
        if (fs.Length > maxSimpleBytes)
            return new List<string> { "[日志文件过大（>16MB），请直接打开 logs 目录下的文件查看。]" };

        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = sr.ReadToEnd();
        var parts = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (parts.Length <= maxLines)
            return parts.Select(s => s.TrimEnd('\r')).ToList();
        return parts.Skip(parts.Length - maxLines).Select(s => s.TrimEnd('\r')).ToList();
    }
}
