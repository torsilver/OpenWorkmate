using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services.Plan;

/// <summary>按约定格式解析计划正文为步骤序列，便于按步注入、不截断。支持「## 步骤 N」标题式与「---」分隔符式。</summary>
public static class PlanStepParser
{
    /// <summary>标题式：每步以 "## 步骤 N" 开头（N 从 1 开始），与计划格式约定一致。</summary>
    private static readonly Regex StepHeadingRegex = new(
        @"^\s*#{1,6}\s*步骤\s*(\d+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>将计划正文解析为步骤列表。优先按「## 步骤 N」切分；若无则按「\n---\n」分隔符切分。步骤顺序与 N 或出现顺序一致，空段忽略。</summary>
    /// <param name="content">计划正文（Markdown）</param>
    /// <returns>步骤文本列表，无步骤时返回空列表</returns>
    public static IReadOnlyList<string> ParsePlanSteps(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<string>();

        var byHeading = ParseByStepHeadings(content);
        if (byHeading.Count > 0)
            return byHeading;

        return ParseByDelimiter(content);
    }

    /// <summary>按「## 步骤 N」标题切分，保留每步完整内容（含标题行）。</summary>
    private static List<string> ParseByStepHeadings(string content)
    {
        var steps = new List<string>();
        var matches = StepHeadingRegex.Matches(content);
        if (matches.Count == 0)
            return steps;

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            var block = content[start..end].Trim();
            if (block.Length > 0)
                steps.Add(block);
        }
        return steps;
    }

    /// <summary>按分隔符 \n---\n 切分（步骤之间用 --- 隔开）。</summary>
    private static List<string> ParseByDelimiter(string content)
    {
        var parts = content.Split(new[] { "\n---\n", "\r\n---\r\n" }, StringSplitOptions.None);
        var steps = new List<string>();
        foreach (var block in parts)
        {
            var t = block.Trim();
            if (t.Length > 0)
                steps.Add(t);
        }
        return steps;
    }

    /// <summary>获取指定步骤（1-based）。若解析结果不足 stepIndex 步则返回 null。</summary>
    public static string? GetStepAt(string? content, int stepIndex)
    {
        if (stepIndex < 1) return null;
        var steps = ParsePlanSteps(content);
        var i = stepIndex - 1;
        return i < steps.Count ? steps[i] : null;
    }
}
