using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 保守判断用户消息是否「很可能需要本机文件/Office 工具落地」；用于首轮零工具调用时的自动重试（见 harness 接地策略）。
/// </summary>
public static class MutationIntentHeuristic
{
    /// <summary>与 <see cref="LikelyRequiresLocalMutationTool"/> 行为概要同步，供测试与遥测。</summary>
    public const string PatternHint =
        "自然语言: 取消合并|合并单元格|写入|删建工作表|另存|改单元格|run_command|设置/调整+行高列宽|行高列宽+设为数字;"
        + " 用户文中 Kernel 函数名( snake_case 或 Plugin.fn )且符合写类命名(与 KernelFunctionNameSemantics 一致)";

    private static readonly Regex MutationLike = new(
        """
        取消合并|
        请合并|要求合并|帮我合并|
        合并(?=[^。！？\n]{0,40}(单元格|区域|Sheet|sheet|工作表|[A-Z]{1,3}\d))|
        写入|写入公式|
        删除工作表|添加工作表|新建工作表|
        另存|保存为|
        修改[^。！？\n]{0,20}单元格|单元格[^。！？\n]{0,20}(写入|修改)|
        公式写入|
        run_command|执行命令|
        (?:设置|调整|改|将)[^。！？\n]{0,30}(?:行高|列宽)|
        (?:行高|列宽)\s*(?:设为|设置为)\s*\d|
        (?:行高|列宽)\s*为\s*\d
        """.ReplaceLineEndings(""),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>Plugin.function 形式中的 function 段（如 Word.word_footer_write → word_footer_write）。</summary>
    private static readonly Regex PluginDotFunction = new(
        @"[A-Za-z][A-Za-z0-9_]*\.([a-z][a-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>独立 snake_case 且至少含一段下划线，近似 Kernel 函数名。</summary>
    private static readonly Regex SnakeKernelLike = new(
        @"(?<![A-Za-z0-9_])([a-z][a-z0-9_]*(?:_[a-z0-9_]+)+)(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// 用户消息是否可能要求对本地文件/Office 产生可验证副作用（需工具调用）。
    /// </summary>
    public static bool LikelyRequiresLocalMutationTool(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;
        var t = userMessage.Trim();
        try
        {
            if (MutationLike.IsMatch(t))
                return true;

            foreach (Match m in PluginDotFunction.Matches(t))
            {
                if (m.Groups.Count > 1 && KernelFunctionNameSemantics.ImpliesLocalMutation(m.Groups[1].Value))
                    return true;
            }

            foreach (Match m in SnakeKernelLike.Matches(t))
            {
                if (m.Groups.Count > 1 && KernelFunctionNameSemantics.ImpliesLocalMutation(m.Groups[1].Value))
                    return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return false;
    }
}
