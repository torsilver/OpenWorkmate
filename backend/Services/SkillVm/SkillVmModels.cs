using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services.SkillVm;

/// <summary>与 user-config / API 对齐的 Skill VM 开关（camelCase 反序列化）。</summary>
public sealed class SkillVmConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>单段注入正文最大字符数；0 表示仅使用全局默认。</summary>
    [JsonPropertyName("maxSegmentChars")]
    public int MaxSegmentChars { get; set; }

    /// <summary>Skill VM 检查点与快照落盘目录；空则使用 %LocalAppData%/OfficeCopilot/SkillVm。</summary>
    [JsonPropertyName("dataDirectory")]
    public string? DataDirectory { get; set; }
}

/// <summary>skill.manifest.json 根对象。</summary>
public sealed class SkillVmManifest
{
    [JsonPropertyName("skillId")]
    public string SkillId { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1";

    [JsonPropertyName("segments")]
    public List<SkillVmSegmentDef> Segments { get; set; } = new();

    /// <summary>允许的 goto 目标（skillId:segmentId）；空则仅允许同 skill 内 manifest 段 id。</summary>
    [JsonPropertyName("allowedGotoTargets")]
    public List<string>? AllowedGotoTargets { get; set; }
}

public sealed class SkillVmSegmentDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>相对技能目录的路径，如 segments/intro.md；空则尝试从 SKILL.md 按标题切分。</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("maxChars")]
    public int? MaxChars { get; set; }
}

/// <summary>会话内 Skill VM 状态（可序列化落盘）。</summary>
public sealed class SkillVmState
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("activeSkillId")]
    public string ActiveSkillId { get; set; } = "";

    [JsonPropertyName("currentSegmentId")]
    public string CurrentSegmentId { get; set; } = "";

    [JsonPropertyName("stack")]
    public List<SkillVmStackFrame> Stack { get; set; } = new();

    [JsonPropertyName("variables")]
    public Dictionary<string, JsonElement> Variables { get; set; } = new();

    [JsonPropertyName("completedSegmentIds")]
    public List<string> CompletedSegmentIds { get; set; } = new();

    [JsonPropertyName("finished")]
    public bool Finished { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }
}

public sealed class SkillVmStackFrame
{
    [JsonPropertyName("skillId")]
    public string SkillId { get; set; } = "";

    [JsonPropertyName("segmentId")]
    public string SegmentId { get; set; } = "";

    [JsonPropertyName("returnSegmentId")]
    public string? ReturnSegmentId { get; set; }
}

/// <summary>模型/工具侧 skill_step 参数（camelCase）。</summary>
public sealed class SkillVmStepArgs
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("targetSkillId")]
    public string? TargetSkillId { get; set; }

    [JsonPropertyName("targetSegmentId")]
    public string? TargetSegmentId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
