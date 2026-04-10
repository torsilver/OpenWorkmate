using System.ComponentModel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 系统信息插件：提供当前时间等只读系统信息，供模型回答用户关于日期、时间的问题。
/// </summary>
[CopilotPluginId("System")]
public sealed class SystemPlugin
{
    [ToolFunction("get_current_time")]
    [Description("Get the current date and time. Call when the user asks about current date, time, today, this week, or any time-related question. Returns ISO 8601 UTC time and local time in human-readable form.")]
    public string GetCurrentTime()
    {
        var utc = DateTime.UtcNow;
        var local = DateTime.Now;
        var utcStr = utc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
        var localStr = local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " (本地)";
        var iso = utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        return $"ISO8601: {iso}\n当前时间（UTC）：{utcStr}\n当前时间（本地）：{localStr}";
    }
}
