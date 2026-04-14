using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;
using Word = Microsoft.Office.Interop.Word;

namespace OfficeCopilot.Server.Plugins;

/// <summary>通过本机已安装的 Microsoft Office COM 将 .doc/.xls/.ppt 另存为 Open XML（仅 net10.0-windows 编译）。</summary>
internal static class OfficeLegacyComInterop
{
    private static async Task StaRunAsync(Action work, int timeoutMs, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Microsoft Office 转换在 {timeoutMs}ms 内未完成（文件过大、损坏或对话框阻塞）。");
        }
    }

    internal static Task RunConversionAsync(
        OfficeLegacyKind kind,
        string inputFullPath,
        string outputFullPath,
        int timeoutMs,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromException(new InvalidOperationException("本工具仅支持在 Windows 上使用。"));

        return StaRunAsync(() =>
        {
            switch (kind)
            {
                case OfficeLegacyKind.Word:
                    ConvertWord(inputFullPath, outputFullPath, logger);
                    break;
                case OfficeLegacyKind.Excel:
                    ConvertExcel(inputFullPath, outputFullPath, logger);
                    break;
                case OfficeLegacyKind.PowerPoint:
                    ConvertPowerPoint(inputFullPath, outputFullPath, logger);
                    break;
                default:
                    throw new InvalidOperationException("未知的旧版文档类型。");
            }
        }, timeoutMs, cancellationToken);
    }

    private static void ConvertWord(string inputFullPath, string outputFullPath, ILogger? logger)
    {
        Word.Application? app = null;
        Word.Document? doc = null;
        try
        {
            app = new Word.Application
            {
                Visible = false,
                DisplayAlerts = Word.WdAlertLevel.wdAlertsNone
            };

            doc = app.Documents.Open(
                FileName: inputFullPath,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false);

            // SaveAs2 在 Word 2010+ 可用；另存为 Open XML 文档
            doc.SaveAs2(
                FileName: outputFullPath,
                FileFormat: Word.WdSaveFormat.wdFormatXMLDocument);
            logger?.LogInformation("[OfficeLegacy] Word 已另存为 {Out}", outputFullPath);
        }
        finally
        {
            if (doc != null)
            {
                try
                {
                    ((Word._Document)doc).Close(SaveChanges: false);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "[OfficeLegacy] Word 关闭文档");
                }

                try { Marshal.FinalReleaseComObject(doc); }
                catch { /* ignored */ }
            }

            if (app != null)
            {
                try
                {
                    app.Quit(SaveChanges: false);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "[OfficeLegacy] Word Quit");
                }

                try { Marshal.FinalReleaseComObject(app); }
                catch { /* ignored */ }
            }
        }
    }

    private static void ConvertExcel(string inputFullPath, string outputFullPath, ILogger? logger)
    {
        Excel.Application? app = null;
        Excel.Workbook? wb = null;
        try
        {
            app = new Excel.Application
            {
                Visible = false,
                DisplayAlerts = false
            };

            wb = app.Workbooks.Open(
                Filename: inputFullPath,
                UpdateLinks: 0,
                ReadOnly: true,
                Format: Type.Missing,
                Password: Type.Missing,
                WriteResPassword: Type.Missing,
                IgnoreReadOnlyRecommended: true,
                Origin: Type.Missing,
                Delimiter: Type.Missing,
                Editable: false,
                Notify: false,
                Converter: Type.Missing,
                AddToMru: false);

            wb.SaveAs(
                Filename: outputFullPath,
                FileFormat: Excel.XlFileFormat.xlOpenXMLWorkbook,
                Password: Type.Missing,
                WriteResPassword: Type.Missing,
                ReadOnlyRecommended: false,
                CreateBackup: Type.Missing);
            logger?.LogInformation("[OfficeLegacy] Excel 已另存为 {Out}", outputFullPath);
        }
        finally
        {
            if (wb != null)
            {
                try
                {
                    wb.Close(SaveChanges: false);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "[OfficeLegacy] Excel 关闭工作簿");
                }

                try { Marshal.FinalReleaseComObject(wb); }
                catch { /* ignored */ }
            }

            if (app != null)
            {
                try
                {
                    app.Quit();
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "[OfficeLegacy] Excel Quit");
                }

                try { Marshal.FinalReleaseComObject(app); }
                catch { /* ignored */ }
            }
        }
    }

    /// <summary>PowerPoint 通过 late-bound COM，避免依赖 <c>Microsoft.Office.Core</c> NuGet（与 PIA 版本对齐困难）。</summary>
    private static void ConvertPowerPoint(string inputFullPath, string outputFullPath, ILogger? logger)
    {
        var pptType = Type.GetTypeFromProgID("PowerPoint.Application", throwOnError: false);
        if (pptType == null)
            throw new InvalidOperationException("未检测到 PowerPoint（ProgID PowerPoint.Application）。请安装 Microsoft Office 中的 PowerPoint。");

        dynamic app = Activator.CreateInstance(pptType) ?? throw new InvalidOperationException("无法创建 PowerPoint.Application。");
        dynamic? pres = null;
        try
        {
            // MsoTriState: msoFalse=0, msoTrue=-1；ppAlertsNone=1
            app.Visible = 0;
            app.DisplayAlerts = 1;
            // Open(FileName, ReadOnly, Untitled, WithWindow)
            pres = app.Presentations.Open(inputFullPath, -1, 0, 0);
            // ppSaveAsOpenXMLPresentation = 24；EmbedTrueTypeFonts = msoTrue = -1
            pres.SaveAs(outputFullPath, 24, -1);
            logger?.LogInformation("[OfficeLegacy] PowerPoint 已另存为 {Out}", outputFullPath);
        }
        finally
        {
            if (pres != null)
            {
                try
                {
                    pres.Close();
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "[OfficeLegacy] PPT 关闭演示文稿");
                }

                try { Marshal.FinalReleaseComObject(pres); }
                catch { /* ignored */ }
            }

            try
            {
                app.Quit();
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "[OfficeLegacy] PPT Quit");
            }

            try { Marshal.FinalReleaseComObject(app); }
            catch { /* ignored */ }
        }
    }
}
