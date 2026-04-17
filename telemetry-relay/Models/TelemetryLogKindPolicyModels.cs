using System.Text.Json.Serialization;

namespace Taskly.Telemetry.Relay.Models;

/// <summary>内置 log 种类元数据（不再使用独立 JSON 文件）；默认 profile 的 <c>logKinds</c> 由此展开，供 GET /policy/aggregated 的 <c>availableLogKinds</c>。</summary>
public sealed class TelemetryLogKindPolicyFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>本部署允许客户端多选上报的种类（与 AI 后台 <c>TelemetryRelayEvent.EventType</c> 对齐）。</summary>
    [JsonPropertyName("kinds")]
    public List<TelemetryLogKindEntry> Kinds { get; set; } = new();
}

public sealed class TelemetryLogKindEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>选项页展示用；缺省为 <see cref="Kind"/>。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public static class TelemetryLogKindPolicyDefaults
{
    /// <summary>与 OfficeCopilot 当前发出的 <c>eventType</c> 一致。</summary>
    public static IReadOnlyList<TelemetryLogKindEntry> BuiltInKinds { get; } =
    [
        new TelemetryLogKindEntry { Kind = "assistant_turn_final", Label = "助手轮次结束" },
        new TelemetryLogKindEntry { Kind = "tool_invocation_end", Label = "工具调用结束" },
        new TelemetryLogKindEntry { Kind = "plan_created", Label = "计划创建" },
        new TelemetryLogKindEntry { Kind = "plan_step_read", Label = "计划步骤读取" },
        new TelemetryLogKindEntry { Kind = "plan_completed", Label = "计划完成" }
    ];

    public static TelemetryLogKindPolicyFile CreateDefault() => new()
    {
        SchemaVersion = 1,
        Kinds = BuiltInKinds.Select(k => new TelemetryLogKindEntry { Kind = k.Kind, Label = k.Label }).ToList()
    };

    public static TelemetryLogKindPolicyFile Merge(TelemetryLogKindPolicyFile? fromFile)
    {
        var d = CreateDefault();
        if (fromFile is null) return d;

        d.SchemaVersion = fromFile.SchemaVersion > 0 ? fromFile.SchemaVersion : d.SchemaVersion;
        if (fromFile.Kinds is not { Count: > 0 }) return d;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<TelemetryLogKindEntry>();
        foreach (var e in fromFile.Kinds)
        {
            var k = (e.Kind ?? "").Trim();
            if (string.IsNullOrEmpty(k) || !seen.Add(k)) continue;
            merged.Add(new TelemetryLogKindEntry
            {
                Kind = k,
                Label = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label.Trim()
            });
        }

        return merged.Count > 0
            ? new TelemetryLogKindPolicyFile { SchemaVersion = d.SchemaVersion, Kinds = merged }
            : d;
    }
}
