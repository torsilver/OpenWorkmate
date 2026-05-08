using Microsoft.Extensions.AI;

namespace OpenWorkmate.Server.Services.ToolInvocation;

/// <summary>
/// 将模型或用户传入的工具名字符串（裸函数名或恰好一段 <c>Plugin.function</c>）解析为注册表中的
/// <c>(pluginId, bareFunctionName)</c>。OpenAPI/MEAI 下发的工具 <c>name</c> 始终为裸函数名。
/// </summary>
public static class ToolQualifiedNameResolver
{
    /// <summary>
    /// 尝试解析 <paramref name="requested"/>。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>无 <c>'.'</c>：按裸名 <c>TryGetPluginName</c>；若跨插件同名则与注册表遍历顺序一致（先命中者胜）。</item>
    /// <item>恰好一个 <c>'.'</c>：拆成 plugin + function，<c>FindTool</c> 须成功。</item>
    /// <item>多个 <c>'.'</c>：拒绝（避免把 <c>func</c> 误拆成多段）。</item>
    /// </list>
    /// </remarks>
    public static bool TryResolve(
        ToolRegistry registry,
        string? requested,
        out string pluginId,
        out string bareFunctionName,
        out AITool? tool)
    {
        pluginId = "";
        bareFunctionName = "";
        tool = null;

        var s = (requested ?? "").Trim();
        if (s.Length == 0)
            return false;

        var dotCount = 0;
        foreach (var c in s)
        {
            if (c == '.')
                dotCount++;
        }

        if (dotCount == 0)
        {
            if (!registry.TryGetPluginName(s, out var pn))
                return false;
            var t = registry.FindTool(pn, s);
            if (t is null)
                return false;
            pluginId = pn;
            bareFunctionName = s;
            tool = t;
            return true;
        }

        if (dotCount != 1)
            return false;

        var dot = s.IndexOf('.');
        var plugin = s[..dot].Trim();
        var func = s[(dot + 1)..].Trim();
        if (plugin.Length == 0 || func.Length == 0)
            return false;

        var tool2 = registry.FindTool(plugin, func);
        if (tool2 is null)
            return false;

        pluginId = plugin;
        bareFunctionName = func;
        tool = tool2;
        return true;
    }
}
