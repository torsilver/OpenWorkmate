namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>
/// 判断 <c>activate_tools</c> 调用结束后是否应刷新可变工具列表（与 ToolInvocationMiddleware 中 TryRefresh 一致）。
/// MEAI 上报的 <c>resolvedFunc</c> 可能是 OpenAPI 名 <c>activate_tools</c> 或 C# 方法名 <c>ActivateToolsAsync</c>。
/// </summary>
public static class DynamicToolingActivateRefreshTriggers
{
    /// <summary>
    /// 与 <see cref="DynamicToolingConstants.ActivateFunctionName"/>（OpenAPI 工具名）或
    /// <see cref="DynamicToolingConstants.ActivateToolsAsyncMethodName"/>（在 AgentTooling 插件上）一致时返回 true。
    /// </summary>
    public static bool ShouldRefreshChatOptionsToolsAfterInvocation(string? pluginName, string? funcName)
    {
        if (string.Equals(funcName, DynamicToolingConstants.ActivateFunctionName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(pluginName, DynamicToolingConstants.AgentToolingPluginId, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(funcName, DynamicToolingConstants.ActivateToolsAsyncMethodName, StringComparison.OrdinalIgnoreCase);
    }
}
