namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>根据返回给模型的工具结果文本粗分失败阶段；与 <see cref="ToolSemanticFailureMarkers"/> 对齐。</summary>
public static class ToolInvocationFailureClassifier
{
    public static ToolInvocationFailureKind Classify(string? fullResult) =>
        ToolSemanticFailureMarkers.ClassifyFailureKind(fullResult);
}
