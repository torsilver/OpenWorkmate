using System.Text.Json.Serialization;

namespace Taskly.AI.Gateway.Models;

/// <summary><c>DataRoot/policy.ops.json</c> 内 <c>policyProfiles</c> 字段形状：多组策略，每组为启用的 AI 流事件种类列表。</summary>
public sealed class TelemetryPolicyProfilesFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("defaultProfileId")]
    public string DefaultProfileId { get; set; } = "default";

    [JsonPropertyName("profiles")]
    public List<TelemetryPolicyProfileEntry> Profiles { get; set; } = new();
}

public sealed class TelemetryPolicyProfileEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("eventKinds")]
    public List<TelemetryEventKindEntry> EventKinds { get; set; } = new();

    /// <summary>与 AI 端 <c>TelemetryIngestDetailGate</c> 对齐：error / warning / information / debug / off；与 bundle 内全局 <c>transmission</c> 上限独立。</summary>
    [JsonPropertyName("ingestLogLevel")]
    public string? IngestLogLevel { get; set; }

    /// <summary>可选：在本 profile 上覆盖 bundle 内全局 <c>transmission</c> 的数值上限（遥测正文/载荷裁剪）。</summary>
    [JsonPropertyName("transmission")]
    public TelemetryTransmissionPolicyFile? Transmission { get; set; }
}

public static class TelemetryPolicyProfilesDefaults
{
    public static TelemetryPolicyProfilesFile CreateFromBuiltinKinds()
    {
        var kinds = TelemetryEventKindPolicyDefaults.BuiltInKinds
            .Select(k => new TelemetryEventKindEntry { Kind = k.Kind, Label = k.Label })
            .ToList();
        return new TelemetryPolicyProfilesFile
        {
            SchemaVersion = 1,
            DefaultProfileId = "default",
            Profiles =
            [
                new TelemetryPolicyProfileEntry
                {
                    Id = "default",
                    Name = "默认（全部 AI 流事件）",
                    EventKinds = kinds
                },
                new TelemetryPolicyProfileEntry
                {
                    Id = "tools-only",
                    Name = "仅工具与计划",
                    EventKinds =
                    [
                        new TelemetryEventKindEntry { Kind = "tool_invocation_end", Label = "工具调用结束" },
                        new TelemetryEventKindEntry { Kind = "plan_created", Label = "计划创建" },
                        new TelemetryEventKindEntry { Kind = "plan_step_read", Label = "计划步骤读取" },
                        new TelemetryEventKindEntry { Kind = "plan_completed", Label = "计划完成" }
                    ]
                }
            ]
        };
    }

    public static TelemetryPolicyProfilesFile Merge(TelemetryPolicyProfilesFile? fromFile)
    {
        var d = CreateFromBuiltinKinds();
        if (fromFile is null) return d;

        var schema = fromFile.SchemaVersion > 0 ? fromFile.SchemaVersion : d.SchemaVersion;
        var defId = string.IsNullOrWhiteSpace(fromFile.DefaultProfileId)
            ? d.DefaultProfileId
            : fromFile.DefaultProfileId.Trim();

        if (fromFile.Profiles is not { Count: > 0 })
        {
            return new TelemetryPolicyProfilesFile
            {
                SchemaVersion = schema,
                DefaultProfileId = defId,
                Profiles = CreateFromBuiltinKinds().Profiles.ToList()
            };
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<TelemetryPolicyProfileEntry>();
        foreach (var p in fromFile.Profiles)
        {
            var id = (p.Id ?? "").Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            var kinds = new List<TelemetryEventKindEntry>();
            foreach (var e in p.EventKinds ?? [])
            {
                var k = (e.Kind ?? "").Trim();
                if (string.IsNullOrEmpty(k)) continue;
                kinds.Add(new TelemetryEventKindEntry { Kind = k, Label = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label.Trim() });
            }

            merged.Add(new TelemetryPolicyProfileEntry
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(p.Name) ? id : p.Name.Trim(),
                EventKinds = kinds,
                IngestLogLevel = string.IsNullOrWhiteSpace(p.IngestLogLevel) ? null : p.IngestLogLevel.Trim(),
                Transmission = p.Transmission
            });
        }

        if (merged.Count == 0)
        {
            return new TelemetryPolicyProfilesFile
            {
                SchemaVersion = schema,
                DefaultProfileId = defId,
                Profiles = CreateFromBuiltinKinds().Profiles.ToList()
            };
        }

        if (!merged.Any(x => string.Equals(x.Id, defId, StringComparison.Ordinal)))
            defId = merged[0].Id;

        return new TelemetryPolicyProfilesFile
        {
            SchemaVersion = schema,
            DefaultProfileId = defId,
            Profiles = merged
        };
    }
}
