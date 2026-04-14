namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>根据返回给模型的工具结果文本粗分失败阶段（启发式，与 <see cref="ToolStatusNotifier"/> 一致）。</summary>
public static class ToolInvocationFailureClassifier
{
    public static ToolInvocationFailureKind Classify(string? fullResult)
    {
        if (string.IsNullOrEmpty(fullResult))
            return ToolInvocationFailureKind.Business;
        if (fullResult.StartsWith("[参数绑定失败]", StringComparison.Ordinal))
            return ToolInvocationFailureKind.Binding;
        if (fullResult.StartsWith("[MCP 工具错误]", StringComparison.Ordinal)
            || fullResult.StartsWith("[MCP 调用异常]", StringComparison.Ordinal)
            || fullResult.StartsWith("[MCP ", StringComparison.Ordinal))
            return ToolInvocationFailureKind.Mcp;
        return ToolInvocationFailureKind.Business;
    }
}
