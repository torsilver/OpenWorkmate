using System.Diagnostics;
using System.Text;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 执行 Clawhub 技能目录下的脚本（如 node scripts/search.mjs），并注入环境变量。
/// 仅允许运行 BaseDir 下 scripts/ 内的 .mjs/.js 文件。
/// </summary>
public sealed class ClawhubScriptRunner
{
    private static readonly string[] AllowedExtensions = { ".mjs", ".js" };
    private const int DefaultTimeoutMs = 60_000;
    private const int MaxOutputLength = 32_000;
    private readonly ILogger<ClawhubScriptRunner> _logger;

    public ClawhubScriptRunner(ILogger<ClawhubScriptRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 在技能目录下执行脚本。脚本路径必须为 scripts/*.mjs 或 scripts/*.js。
    /// </summary>
    /// <param name="baseDir">技能根目录（BaseDir）</param>
    /// <param name="scriptRelativePath">相对于 baseDir 的脚本路径，如 scripts/search.mjs</param>
    /// <param name="args">命令行参数列表</param>
    /// <param name="envOverrides">要注入的环境变量（会覆盖或补充当前进程环境）</param>
    /// <param name="timeoutMs">超时毫秒数</param>
    public async Task<string> RunAsync(
        string baseDir,
        string scriptRelativePath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? envOverrides = null,
        int timeoutMs = DefaultTimeoutMs)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
            return "[错误] 技能目录不存在或无效。";

        var normalized = scriptRelativePath.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
            return "[错误] 仅允许运行 skills 下的 scripts/ 目录内的脚本。";

        var ext = Path.GetExtension(normalized);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return "[错误] 仅支持 .mjs 或 .js 脚本。";

        var fullPath = Path.GetFullPath(Path.Combine(baseDir, normalized));
        var baseDirFull = Path.GetFullPath(baseDir);
        if (!fullPath.StartsWith(baseDirFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return "[错误] 脚本路径无效或文件不存在。";

        var nodePath = FindNodePath();
        if (string.IsNullOrEmpty(nodePath))
            return "[错误] 未找到 node 可执行文件，请确保已安装 Node.js 并加入 PATH。";

        var arguments = string.Join(" ", args.Select(EscapeArg));
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            Arguments = $"\"{fullPath}\" {arguments}".Trim(),
            WorkingDirectory = baseDirFull,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var (key, value) in GetMergedEnv(envOverrides))
        {
            startInfo.Environment[key] = value;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            using var cts = new CancellationTokenSource(timeoutMs);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var output = string.IsNullOrEmpty(stderr)
                ? stdout
                : $"{stdout}\n[STDERR]: {stderr}";

            if (output.Length > MaxOutputLength)
                output = output[..MaxOutputLength] + $"\n...(输出已截断，共 {output.Length} 字符)";

            return process.ExitCode != 0
                ? $"[退出码: {process.ExitCode}]\n{output}".Trim()
                : output.Trim();
        }
        catch (OperationCanceledException)
        {
            return $"[错误] 脚本执行超时（{timeoutMs}ms）。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clawhub script run failed: {BaseDir} {Script}", baseDir, scriptRelativePath);
            return $"[错误] 执行脚本失败: {ex.Message}";
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> GetMergedEnv(IReadOnlyDictionary<string, string>? overrides)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
        {
            if (key is string k && value is string v)
                env[k] = v;
        }
        if (overrides != null)
        {
            foreach (var (k, v) in overrides)
                env[k] = v;
        }
        return env;
    }

    private static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains('"') || arg.Contains(' ') || arg.Contains('\n'))
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return arg;
    }

    private static string? FindNodePath()
    {
        var node = Environment.GetEnvironmentVariable("NODE_PATH");
        if (!string.IsNullOrWhiteSpace(node) && File.Exists(node))
            return node;
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var nodeExe = Path.Combine(dir.Trim(), "node.exe");
            if (File.Exists(nodeExe)) return nodeExe;
            nodeExe = Path.Combine(dir.Trim(), "node");
            if (File.Exists(nodeExe)) return nodeExe;
        }
        return null;
    }
}
