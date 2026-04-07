using System.Text;
using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services.Plan;

/// <summary>将本轮选中的 <see cref="AITool"/> 格式化为计划撰写提示中的「可用工具」附录（可截断）。</summary>
public static class PlanAuthoringToolDigest
{
    public const int DefaultMaxTotalChars = 48_000;
    public const int MaxDescriptionCharsPerTool = 120;

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
            var desc = (tool.Description ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (desc.Length > MaxDescriptionCharsPerTool)
                desc = desc[..MaxDescriptionCharsPerTool] + "…";
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
}
