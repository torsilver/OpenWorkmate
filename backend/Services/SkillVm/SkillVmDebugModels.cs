using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services.SkillVm;

/// <summary>调试会话标志（内存存储，按 sessionId）。</summary>
public sealed class SkillVmDebugFlags
{
    [JsonPropertyName("pauseBeforeInject")]
    public bool PauseBeforeInject { get; set; }

    /// <summary>为 true 时，每次 skill_step 成功后宿主将 <see cref="SkillVmState.Paused"/> 置为 true。</summary>
    [JsonPropertyName("pauseAfterSkillStep")]
    public bool PauseAfterSkillStep { get; set; }
}

public sealed class SkillVmDebugFlagsRequest
{
    [JsonPropertyName("pauseBeforeInject")]
    public bool PauseBeforeInject { get; set; }

    [JsonPropertyName("pauseAfterSkillStep")]
    public bool PauseAfterSkillStep { get; set; }
}

public sealed class SkillVmDebugSessionResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("state")]
    public SkillVmState? State { get; set; }

    /// <summary>将注入模型的 Skill VM 块（含当前段正文）。</summary>
    [JsonPropertyName("injectionPreview")]
    public string? InjectionPreview { get; set; }

    [JsonPropertyName("estimatedTokens")]
    public int EstimatedTokens { get; set; }

    [JsonPropertyName("flags")]
    public SkillVmDebugFlags Flags { get; set; } = new();

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
