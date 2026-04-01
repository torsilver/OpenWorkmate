using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 保守判断用户消息是否「很可能需要本机文件/Office 工具落地」；用于首轮零工具调用时的自动重试（见 harness 接地策略）。
/// </summary>
public static class MutationIntentHeuristic
{
    /// <summary>与 <see cref="LikelyRequiresLocalMutationTool"/> 同步维护，供测试与遥测。</summary>
    public const string PatternHint =
        "取消合并|请合并|要求合并|帮我合并|合并+单元格/区域/Sheet|写入|删除工作表|添加工作表|另存|保存为|修改单元格|公式写入|run_command";

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
        run_command|执行命令
        """.ReplaceLineEndings(""),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// 用户消息是否可能要求对本地文件/Office 产生可验证副作用（需工具调用）。
    /// </summary>
    public static bool LikelyRequiresLocalMutationTool(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;
        try
        {
            return MutationLike.IsMatch(userMessage.Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
