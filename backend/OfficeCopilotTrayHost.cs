using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using OfficeCopilot.Server.Services;
using Microsoft.Extensions.Hosting;

namespace OfficeCopilot.Server;

/// <summary>Windows 托盘：同进程内启动 WebApplication.RunAsync 并跑 WinForms 消息循环。</summary>
internal static class OfficeCopilotTrayHost
{
    public static void Run(WebApplication app, string logViewerUrl)
    {
        ApplicationConfiguration.Initialize();
        using var ctx = new TrayAppContext(app, logViewerUrl);
        Application.Run(ctx);
    }

    private sealed class TrayAppContext : ApplicationContext
    {
        private readonly WebApplication _app;
        private readonly string _logViewerUrl;
        private readonly NotifyIcon _icon;
        private Task? _runTask;

        public TrayAppContext(WebApplication app, string logViewerUrl)
        {
            _app = app;
            _logViewerUrl = logViewerUrl;
            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "Office Copilot 后台"
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("设置", null, (_, _) => OpenSettings());
            menu.Items.Add("调试日志", null, (_, _) => OpenLogPage());
            menu.Items.Add("退出", null, (_, _) => ExitFromMenu());
            _icon.ContextMenuStrip = menu;

            var lifetime = _app.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopped.Register(() =>
            {
                try
                {
                    if (_icon.ContextMenuStrip != null && _icon.ContextMenuStrip.InvokeRequired)
                        _icon.ContextMenuStrip.BeginInvoke(ExitThreadCore);
                    else
                        ExitThreadCore();
                }
                catch
                {
                    ExitThreadCore();
                }
            });

            _runTask = _app.RunAsync();
            _ = _runTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        var msg = t.Exception?.GetBaseException().Message ?? "未知错误";
                        try
                        {
                            MessageBox.Show("后台启动失败：\n" + msg, "Office Copilot", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        catch { /* ignore */ }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void OpenLogPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _logViewerUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开调试日志页：\n" + ex.Message + "\n\n请手动在浏览器打开：\n" + _logViewerUrl,
                    "Office Copilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenSettings()
        {
            string? id = null;
            try
            {
                id = _app.Services.GetRequiredService<ConfigService>().Current.ChromeExtensionId?.Trim();
            }
            catch
            {
                /* ignored */
            }

            if (string.IsNullOrEmpty(id))
            {
                MessageBox.Show(
                    "未配置 Chrome 扩展 ID。\n\n请在 user-config.json 中设置 chromeExtensionId。\n扩展 ID 可在 Chrome 打开 chrome://extensions 并开启「开发者模式」后从列表中复制。",
                    "Office Copilot",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (id.Length != 32 || id.Any(c => !char.IsLetterOrDigit(c)))
            {
                MessageBox.Show(
                    "Chrome 扩展 ID 格式异常（应为 32 位字母数字）。当前值：" + id,
                    "Office Copilot",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var url = "chrome-extension://" + id + "/options.html";
            var chrome = FindChromeExecutable();
            try
            {
                if (!string.IsNullOrEmpty(chrome))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = chrome,
                        Arguments = url,
                        UseShellExecute = false
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "无法打开扩展设置页：\n" + ex.Message +
                    "\n\n请安装 Google Chrome，或手动在 Chrome 地址栏打开：\n" + url,
                    "Office Copilot",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static string? FindChromeExecutable()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var c in new[]
                     {
                         Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                         Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe")
                     })
            {
                if (File.Exists(c)) return c;
            }

            return null;
        }

        private void ExitFromMenu()
        {
            try
            {
                _app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            }
            catch (Exception ex)
            {
                MessageBox.Show("停止服务时出错：\n" + ex.Message, "Office Copilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ExitThreadCore();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
