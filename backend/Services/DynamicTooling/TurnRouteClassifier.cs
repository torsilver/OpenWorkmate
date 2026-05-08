using System.Text.RegularExpressions;

namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>v1 启发式路由：无额外模型调用。</summary>
public static partial class TurnRouteClassifier
{
    private static readonly string[] TaskKeywords =
    [
        "文档", "word", "excel", "表格", "单元格", "工作表", "ppt", "幻灯片", "pdf",
        "读取", "写入", "保存", "打开", "文件", "路径", "文件夹", "脚本", "宏",
        "运行", "命令", "浏览器", "网页", "侧栏", "计划", "步骤", "执行",
        "附件", "图片", "ocr", "公式", "替换", "查找", "合并", "拆分",
    ];

    /// <summary>
    /// 根据用户文本与是否绑定计划等信号生成本轮路由。
    /// </summary>
    public static TurnRoute Classify(string? userMessage, bool hasBoundPlan)
    {
        if (hasBoundPlan)
            return TurnRoute.TaskOriented;

        var u = userMessage?.Trim() ?? "";
        if (u.Length == 0)
            return TurnRoute.UnclearOrChitchat;

        if (AttachmentRef().IsMatch(u))
            return TurnRoute.TaskOriented;

        if (LooksLikeTaskKeywords(u))
            return TurnRoute.TaskOriented;

        if (IsUnclearShortOrNoise(u))
            return TurnRoute.UnclearOrChitchat;

        return TurnRoute.Standard;
    }

    /// <summary>较长且含任务类关键词时视为「任务向」用户句（供 Verifier 门控）。</summary>
    public static bool LooksLikeTaskUserMessage(string? userMessage)
    {
        var u = userMessage?.Trim() ?? "";
        return u.Length >= 8 && LooksLikeTaskKeywords(u);
    }

    private static bool LooksLikeTaskKeywords(string u)
    {
        var lower = u.ToLowerInvariant();
        foreach (var k in TaskKeywords)
        {
            if (u.Contains(k, StringComparison.Ordinal) || lower.Contains(k.ToLowerInvariant()))
                return true;
        }

        return false;
    }

    private static bool IsUnclearShortOrNoise(string u)
    {
        if (u.Length > 24)
            return false;

        // 纯数字、标点、空白
        if (DigitsPunctOnly().IsMatch(u))
            return true;

        // 极短且无中日韩字母类字符
        if (u.Length <= 6 && !ContainsLooseWordChars(u))
            return true;

        return false;
    }

    private static bool ContainsLooseWordChars(string u)
    {
        foreach (var c in u)
        {
            if (char.IsLetter(c))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"attachment\s*:\s*\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentRef();

    [GeneratedRegex(@"^[\d\s\p{P}]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsPunctOnly();
}
