using System.Text;
using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.Plan;

/// <summary>将本轮选中的 <see cref="AITool"/> 格式化为计划撰写提示中的「可用工具」附录（可截断）。</summary>
public static class PlanAuthoringToolDigest
{
    public const int DefaultMaxTotalChars = 48_000;
    /// <summary>单条工具描述上限（含 <see cref="ToolCapabilityRegistry"/> 风险尾标）。</summary>
    public const int MaxDescriptionCharsPerTool = 280;

    /// <summary>每行 <c>Plugin.function: 描述</c>；超长时截断并追加说明行。</summary>
    public static string Build(IReadOnlyList<AITool> tools, ToolRegistry registry, int maxTotalChars = DefaultMaxTotalChars)
    {
        if (tools == null || tools.Count == 0)
            return "（无可用工具列表；请仅写不依赖具体 API 的通用准备步骤，或说明须由用户补充路径与约束。）";

        var sb = new StringBuilder();
        var omitted = 0;
        foreach (var tool in tools)
        {
            if (!registry.TryGetPluginName(tool.Name, out var plugin))
                plugin = "?";
            var desc = FormatOneLineDescriptionWithRiskHint(plugin, tool.Name, tool.Description ?? "");
            var line = $"- {plugin}.{tool.Name}: {desc}";
            if (sb.Length + line.Length + 1 > maxTotalChars)
            {
                omitted++;
                continue;
            }
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line);
        }
        if (omitted > 0)
            sb.AppendLine().Append("(以上附录因长度限制省略 ").Append(omitted).Append(" 个工具；仍须遵守：步骤只能依赖本端 Office Copilot 已暴露的工具能力，不得假设附录外手段。)");
        return sb.ToString();
    }

    internal static string FormatOneLineDescriptionWithRiskHint(string plugin, string functionName, string rawDescription)
    {
        var desc = rawDescription.Trim().Replace('\r', ' ').Replace('\n', ' ');
        var cap = ToolCapabilityRegistry.Get(plugin, functionName);
        string hint = "";
        if (cap.SuggestHitl)
            hint = "[需确认/HITL]";
        else if (cap.Destructive)
            hint = "[写盘或有副作用]";

        if (hint.Length == 0)
        {
            if (desc.Length > MaxDescriptionCharsPerTool)
                return desc[..MaxDescriptionCharsPerTool] + "…";
            return desc;
        }

        var budget = MaxDescriptionCharsPerTool - hint.Length - 1;
        if (budget < 16)
            return hint;
        if (desc.Length > budget)
            desc = desc[..budget] + "…";
        return string.IsNullOrEmpty(desc) ? hint : desc + " " + hint;
    }
}
