namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>主会话「动态工具」：LangGraph 式阶段 + Anthropic 式按需检索/激活。</summary>
public sealed class DynamicToolingConfig
{
    /// <summary>为 false 时回退为单次 MAF 流式 + 全量允许工具（旧行为）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>外层循环：每轮可扩容 Tools 后重新 RunStreamingAsync；防止无限循环。</summary>
    public int MaxOuterLoops { get; set; } = 4;

    public int MaxSearchPerTurn { get; set; } = 12;

    public int MaxActivatePerTurn { get; set; } = 48;

    /// <summary>若所有外层轮结束后从未激活任何非引导工具，则用全量允许列表再跑一轮 agent（提高可用性）。</summary>
    public bool FallbackToFullAllowlistWhenNoActivation { get; set; } = true;
}
