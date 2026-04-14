namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>工具调用失败阶段（用于调试统计聚合，非对外 API 契约）。</summary>
public enum ToolInvocationFailureKind
{
    Binding,
    Mcp,
    Business,
}
