using System.Text.Json.Serialization;
using OfficeCopilot.Server.Services.SkillVm;

namespace SkillDebugger.Cli;

public sealed class TraceFileDto
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("skillId")]
    public string SkillId { get; set; } = "";

    [JsonPropertyName("entries")]
    public List<TraceEntryDto> Entries { get; set; } = new();
}

public sealed class TraceEntryDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("skillStep")]
    public SkillVmStepArgs? SkillStep { get; set; }

    [JsonPropertyName("stateAfter")]
    public SkillVmState? StateAfter { get; set; }
}
