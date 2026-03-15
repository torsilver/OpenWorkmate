using System.ComponentModel;
using System.Diagnostics;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Plugins;

public sealed class CliPlugin
{
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxOutputLength = 8_000;

    [KernelFunction("run_command")]
    [Description("运行位置：用户本机（后端服务所在机器）的 CMD。在用户 Windows 电脑上执行一条 CMD 命令并返回输出，适用于查看文件列表、系统信息、执行脚本等。当没有更合适的专用工具时可用作兜底。禁止执行删除、格式化等危险操作。")]
    public async Task<string> RunCommandAsync(
        [Description("要执行的 CMD 命令，例如 dir D:\\、echo hello、type file.txt")] string command,
        [Description("超时时间（毫秒），默认 30000")] int timeoutMs = DefaultTimeoutMs)
    {
        try
        {
            // Windows 中文版 cmd 默认代码页是 GBK (936)
            // 需要在 .NET Core 中注册 CodePages 才能使用 GBK
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding("gbk"),
                StandardErrorEncoding = System.Text.Encoding.GetEncoding("gbk")
            };

            process.Start();

            using var cts = new CancellationTokenSource(timeoutMs);
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var output = string.IsNullOrEmpty(stderr)
                ? stdout
                : $"{stdout}\n[STDERR]: {stderr}";

            if (output.Length > MaxOutputLength)
                output = output[..MaxOutputLength] + $"\n...(输出已截断，共 {output.Length} 字符)";

            return $"[Exit Code: {process.ExitCode}]\n{output}".Trim();
        }
        catch (OperationCanceledException)
        {
            return $"[错误] 命令执行超时（{timeoutMs}ms）: {command}";
        }
        catch (Exception ex)
        {
            return $"[错误] 执行命令失败: {ex.Message}";
        }
    }
}
