namespace OfficeCopilot.Server.Services.ModelProfiles;

/// <summary>LiteLLM 摘录字段与 taskly-overlay 合并后的只读视图。</summary>
public sealed class MergedModelProfile
{
    public required string ProfileKey { get; init; }

    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool SupportsFunctionCalling { get; init; }
    public bool SupportsVision { get; init; }
    public bool SupportsReasoning { get; init; }

    /// <summary>上游在 thinking + 工具多轮时是否要求回传 reasoning（诊断/策略标记）。</summary>
    public bool RequiresReasoningEchoWithTools { get; init; }

    /// <summary>在检测到 assistant tool_calls 且缺少 reasoning_content 时，出站请求顶层写入 <c>thinking: false</c>。</summary>
    public bool SuppressUpstreamThinkingWithTools { get; init; }

    public bool? RecommendedEnableThinking { get; init; }
    public int? RecommendedThinkingBudget { get; init; }
    public string? Notes { get; init; }
}
