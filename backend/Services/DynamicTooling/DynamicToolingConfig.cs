using System.Text.Json.Serialization;

namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>主会话「动态工具」：LangGraph 式阶段 + Anthropic 式按需检索/激活（主路径唯一方式）。</summary>
public sealed class DynamicToolingConfig
{
    /// <summary>外层循环：每轮可扩容 Tools 后重新 RunStreamingAsync；防止无限循环。</summary>
    [JsonPropertyName("maxOuterLoops")]
    public int MaxOuterLoops { get; set; } = 4;

    [JsonPropertyName("maxSearchPerTurn")]
    public int MaxSearchPerTurn { get; set; } = 12;

    [JsonPropertyName("maxActivatePerTurn")]
    public int MaxActivatePerTurn { get; set; } = 48;
}
