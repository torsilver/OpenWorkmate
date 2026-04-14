using System.ComponentModel;
using System.Runtime.InteropServices;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>将 Office 97–2003 二进制格式（.doc/.dot/.xls/.ppt）通过本机已安装的 Microsoft Office 另存为 Open XML，供后续 Word/Excel/PPT 内核工具使用。</summary>
[CopilotPluginId("OfficeLegacy")]
public sealed class OfficeLegacyConvertPlugin
{
    private const int DefaultTimeoutMs = 90_000;
    private readonly ILogger<OfficeLegacyConvertPlugin>? _logger;

    public OfficeLegacyConvertPlugin(ILogger<OfficeLegacyConvertPlugin>? logger = null) => _logger = logger;

    [ToolFunction("office_legacy_save_as_open_xml")]
    [Description(
        "在用户本机（后端所在 Windows）通过已安装的 Microsoft Office（Word/Excel/PowerPoint）将旧版二进制文件另存为 Open XML：.doc/.dot→.docx，.xls→.xlsx，.ppt→.pptx。"
        + " 须先调用本工具再使用 word_body_read、excel_*、ppt_* 等仅支持 Open XML 的工具。"
        + " 前置条件：Windows、后台为 net10.0-windows 构建、已安装对应 Office 组件；可能短暂启动 Office 进程（无窗口）。"
        + " 失败时请把返回的完整说明转述给用户，并建议其在 Office 中手动「另存为」新格式。")]
    public async Task<string> OfficeLegacySaveAsOpenXmlAsync(
        [Description("要转换的本地文件完整路径（可为环境变量或相对路径，将解析到用户下载目录）")] string inputPath,
        [Description("输出文件完整路径；省略则在同目录生成「原名_converted.docx」等，且不会覆盖已存在文件")] string? outputPath = null,
        [Description("超时毫秒数，默认 90000")] int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
#if !NET10_0_WINDOWS
        return "[错误] 当前 Taskly 后台未以 net10.0-windows 构建，未启用 Microsoft Office COM 转换。请使用 Windows 专用发行版后台。";
#else
        if (!OperatingSystem.IsWindows())
            return "[错误] 本工具仅支持在 Windows 上运行。";

        if (string.IsNullOrWhiteSpace(inputPath))
            return "[错误] 请提供 inputPath。";

        if (timeoutMs < 5_000 || timeoutMs > 600_000)
            return "[错误] timeoutMs 须在 5000 至 600000 之间。";

        var resolvedIn = OpenXmlHelpers.ResolvePath(inputPath.Trim());
        string inputFull;
        try
        {
            inputFull = Path.GetFullPath(resolvedIn);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return "[错误] inputPath 无效: " + ex.Message;
        }

        if (!OfficeLegacyConversionHelper.TryGetLegacyKind(inputFull, out var kind))
            return "[错误] 仅支持输入扩展名为 .doc、.dot、.xls、.ppt 的文件。若为 .docx/.xlsx/.pptx 请直接使用对应 Open XML 工具。";

        if (!OfficeLegacyConversionHelper.InputFileExists(inputFull, out var inErr))
            return inErr!;

        string outputFull;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            if (!OfficeLegacyConversionHelper.TryBuildDefaultOutputPath(inputFull, kind, out var defOut, out var defErr))
                return defErr!;
            outputFull = defOut!;
        }
        else
        {
            var resolvedOut = OpenXmlHelpers.ResolvePath(outputPath.Trim());
            try
            {
                outputFull = Path.GetFullPath(resolvedOut);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return "[错误] outputPath 无效: " + ex.Message;
            }

            if (!OfficeLegacyConversionHelper.ValidateOutputExtension(outputFull, kind, out var extErr))
                return extErr!;

            if (File.Exists(outputFull))
                return "[错误] 输出文件已存在，为避免覆盖请更换 outputPath 或先删除: " + outputFull;
        }

        var outDir = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                return "[错误] 无法创建输出目录: " + ex.Message;
            }
        }

        _logger?.LogInformation("[OfficeLegacy] 开始转换 kind={Kind} in={In} out={Out} timeoutMs={Ms}", kind, inputFull, outputFull, timeoutMs);

        try
        {
            await OfficeLegacyComInterop.RunConversionAsync(kind, inputFull, outputFull, timeoutMs, cancellationToken, _logger)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger?.LogWarning(ex, "[OfficeLegacy] 超时");
            return "[错误] " + ex.Message;
        }
        catch (COMException ex)
        {
            _logger?.LogWarning(ex, "[OfficeLegacy] COM 异常");
            return "[错误] Microsoft Office 转换失败（COM）: " + ex.Message + "（HRESULT=0x" + ex.HResult.ToString("X8") + "）。请确认已安装对应组件、文件未损坏、未被其他程序占用。";
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogWarning(ex, "[OfficeLegacy] 无效操作");
            return "[错误] " + ex.Message;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OfficeLegacy] 未分类异常");
            return "[错误] 转换失败: " + ex.Message;
        }

        return "已转换并保存到: " + outputFull + "。请对该路径继续使用 Open XML 工具（如 word_body_read、excel_range_read、ppt_slides_list 等）。";
#endif
    }
}
