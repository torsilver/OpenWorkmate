using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>主会话「动态工具」：LangGraph 式阶段 + Anthropic 式按需检索/激活。</summary>
public sealed class DynamicToolingConfig
{
    /// <summary>为 false 时回退为单次 MAF 流式 + 全量允许工具（旧行为）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>外层循环：每轮可扩容 Tools 后重新 RunStreamingAsync；防止无限循环。</summary>
    [JsonPropertyName("maxOuterLoops")]
    public int MaxOuterLoops { get; set; } = 4;

    [JsonPropertyName("maxSearchPerTurn")]
    public int MaxSearchPerTurn { get; set; } = 12;

    [JsonPropertyName("maxActivatePerTurn")]
    public int MaxActivatePerTurn { get; set; } = 48;

    /// <summary>每轮 <c>search_available_skills</c> 调用次数上限。</summary>
    [JsonPropertyName("maxSkillSearchPerTurn")]
    public int MaxSkillSearchPerTurn { get; set; } = 12;

    /// <summary>每轮 <c>select_skill_for_turn</c> 调用次数上限（每次调用计 1，与 activate_tools 一致）。</summary>
    [JsonPropertyName("maxSkillSelectPerTurn")]
    public int MaxSkillSelectPerTurn { get; set; } = 8;

    /// <summary>为 true 时，<c>load_user_skill_instructions</c> 仅允许加载已在 <c>select_skill_for_turn</c> 中选中的技能 Id。</summary>
    [JsonPropertyName("requireSkillSelectBeforeLoad")]
    public bool RequireSkillSelectBeforeLoad { get; set; }

    /// <summary>若所有外层轮结束后从未激活任何非引导工具，则用全量允许列表再跑一轮 agent（提高可用性）。</summary>
    [JsonPropertyName("fallbackToFullAllowlistWhenNoActivation")]
    public bool FallbackToFullAllowlistWhenNoActivation { get; set; } = true;

    /// <summary>
    /// 已废弃：渐进式 UserSkill 下首轮固定含 <c>load_user_skill_instructions</c>，技能发现见 system 元数据块。
    /// 若配置仍非空，运行时会打警告并忽略。
    /// </summary>
    [JsonPropertyName("bootstrapUserSkillIds")]
    public List<string> BootstrapUserSkillIds { get; set; } = new();

    /// <summary>
    /// 已废弃：同上；不再向 bootstrap 注入 per-skill 工具。
    /// </summary>
    [JsonPropertyName("bootstrapIncludeAllEnabledUserSkills")]
    public bool BootstrapIncludeAllEnabledUserSkills { get; set; }
}
