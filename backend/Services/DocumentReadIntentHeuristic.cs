using System.Text.RegularExpressions;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 判断用户消息是否显式点名「Office/当前文档」类只读 Kernel，用于首轮零工具时的读工具接地重试（与 <see cref="MutationIntentHeuristic"/> 对称）。
/// </summary>
public static class DocumentReadIntentHeuristic
{
    private static readonly Regex PluginDotFunction = new(
        @"[A-Za-z][A-Za-z0-9_]*\.([a-z][a-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex SnakeKernelLike = new(
        @"(?<![A-Za-z0-9_])([a-z][a-z0-9_]*(?:_[a-z0-9_]+)+)(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// 用户消息是否显式包含需本机读工具落地的 Office/文档只读函数名（如 word_body_read、ppt_slide_read）。
    /// </summary>
    public static bool LikelyRequiresDocumentReadTool(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;
        var t = userMessage.Trim();
        try
        {
            foreach (Match m in PluginDotFunction.Matches(t))
            {
                if (m.Groups.Count > 1 && IsOfficeDocumentReadKernel(m.Groups[1].Value))
                    return true;
            }

            foreach (Match m in SnakeKernelLike.Matches(t))
            {
                if (m.Groups.Count > 1 && IsOfficeDocumentReadKernel(m.Groups[1].Value))
                    return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return false;
    }

    private static bool IsOfficeDocumentReadKernel(string fn)
    {
        if (string.IsNullOrEmpty(fn))
            return false;
        var word = fn.StartsWith("word_", StringComparison.OrdinalIgnoreCase);
        var excel = fn.StartsWith("excel_", StringComparison.OrdinalIgnoreCase);
        var ppt = fn.StartsWith("ppt_", StringComparison.OrdinalIgnoreCase);
        var current = fn.StartsWith("current_", StringComparison.OrdinalIgnoreCase);
        if (!word && !excel && !ppt && !current)
            return false;

        // CurrentDocument：read_body / read_range 等不满足「后缀 _read/_list」但仍是只读
        if (current)
        {
            if (KernelFunctionNameSemantics.IsLikelyDestructive(fn))
                return false;
            return KernelFunctionNameSemantics.IsLikelyReadOnly(fn)
                   || fn.Contains("_read", StringComparison.OrdinalIgnoreCase)
                   || fn.Contains("_list", StringComparison.OrdinalIgnoreCase);
        }

        return KernelFunctionNameSemantics.IsLikelyReadOnly(fn);
    }
}
