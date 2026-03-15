using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services.Plan;

/// <summary>计划元数据：标题、状态、时间。</summary>
public class PlanMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "draft"; // draft | in_progress | done

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>创建该计划的端：chrome | office-word | office-excel | wps。</summary>
    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "";

    /// <summary>创建该计划时的 Agent 显示名（如页面标题），用于按 Agent 筛选。</summary>
    [JsonPropertyName("createdByDisplayName")]
    public string CreatedByDisplayName { get; set; } = "";

    /// <summary>是否需用户确认后再执行（由后台规则根据步数阈值、敏感工具等计算）。</summary>
    [JsonPropertyName("requiresUserConfirmation")]
    public bool RequiresUserConfirmation { get; set; }
}
