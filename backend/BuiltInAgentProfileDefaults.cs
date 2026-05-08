using System.Linq;

namespace OfficeCopilot.Server;

/// <summary>
/// 侧栏 Agent 内置预设：与 user-config 合并时<strong>缺什么补什么</strong>（同 id 以用户配置为准），
/// 与 Chrome / WPS 在无列表时的回退语义一致。
/// </summary>
public static class BuiltInAgentProfileDefaults
{
    /// <summary>
    /// 按内置 id 顺序输出；每个 id 若用户有定义则用用户条目，否则用内置；
    /// 用户自定义的、不在内置表中的 id 按原顺序接在末尾。
    /// </summary>
    public static List<AgentProfileEntry> MergeWithUserProfiles(IReadOnlyList<AgentProfileEntry> deduplicatedUser)
    {
        if (deduplicatedUser == null || deduplicatedUser.Count == 0)
            return new List<AgentProfileEntry>(CreateList());

        var builtIns = CreateList();
        var builtInIds = new HashSet<string>(builtIns.Select(static b => b.Id), StringComparer.OrdinalIgnoreCase);
        var merged = new List<AgentProfileEntry>(builtIns.Count + deduplicatedUser.Count);

        foreach (var b in builtIns)
        {
            AgentProfileEntry? chosen = null;
            foreach (var x in deduplicatedUser)
            {
                if (string.Equals(x.Id, b.Id, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = x;
                    break;
                }
            }

            merged.Add(chosen ?? b);
        }

        foreach (var u in deduplicatedUser)
        {
            if (builtInIds.Contains(u.Id)) continue;
            if (merged.Exists(p => string.Equals(p.Id, u.Id, StringComparison.OrdinalIgnoreCase))) continue;
            merged.Add(u);
        }

        return merged;
    }

    public static List<AgentProfileEntry> CreateList() =>
    [
        new AgentProfileEntry { Id = "default", DisplayName = "默认助手", SystemPromptSuffix = null },
        new AgentProfileEntry
        {
            Id = "moe",
            DisplayName = "萌萌助手",
            SystemPromptSuffix =
                "你是「萌萌助手」：请用轻松软萌、亲切的语气与用户交流，可适度使用「呀、呢、喔」等语气词与少量颜文字。回答须真实准确；涉及安全、法律、医疗等专业问题时仍要严谨说明，勿用卖萌代替依据。"
        },
        new AgentProfileEntry
        {
            Id = "ceo",
            DisplayName = "霸气总裁",
            SystemPromptSuffix =
                "你是「霸道总裁」人设：语气自信果断、略带距离感但保持礼貌，表述简短有力。遇到不确定或风险时必须明确说明，勿用强硬口吻掩盖；禁止油腻、骚扰、贬低用户。"
        },
        new AgentProfileEntry
        {
            Id = "pro",
            DisplayName = "专业高手",
            SystemPromptSuffix =
                "你是「专业高手」：像资深顾问一样先给结论再讲依据，分条说明，可执行、可验证；避免空话套话，语气克制专业。"
        },
        new AgentProfileEntry
        {
            Id = "poet",
            DisplayName = "文艺诗人",
            SystemPromptSuffix =
                "你是「文艺诗人」：在准确、清楚的前提下，可用略带文采与意象的中文表达；避免生僻字堆砌，复杂说明仍要层次分明。"
        },
        new AgentProfileEntry
        {
            Id = "roast",
            DisplayName = "损友吐槽",
            SystemPromptSuffix =
                "你是「损友」型搭档：可先一两句轻松吐槽再认真解决问题，俏皮不刻薄；禁止人身攻击、歧视、低俗；涉及安全与合规须立即严肃说明。"
        },
        new AgentProfileEntry
        {
            Id = "zen",
            DisplayName = "冷静极简",
            SystemPromptSuffix =
                "你是「冷静极简」：能用一句话就不用两句，去掉客套与多余感叹；仍须准确、完整地覆盖用户问题的实质要点。"
        }
    ];
}
