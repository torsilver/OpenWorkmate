using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Services.ToolInvocation;

/// <summary>
/// 将工具管道内异常格式化为返回给模型的短文案（不含堆栈）；取消类异常由中间件直接 rethrow，不经过本类。
/// </summary>
public static class ToolInvocationFailureFormatter
{
    /// <summary>嵌入文案中的异常 Message 最大长度，避免过长路径或内部串撑爆上下文。</summary>
    public const int MaxDetailChars = 512;

    /// <summary>
    /// 是否应向上抛出、不把结果写回对话（用户/宿主取消或协作取消 token）。
    /// </summary>
    public static bool ShouldRethrowAsCancellation(Exception ex, CancellationToken cancellationToken) =>
        ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>
    /// 生成供模型阅读的工具失败说明；绑定/参数类异常保留 <paramref name="ex"/>.Message 便于自纠。
    /// </summary>
    public static string FormatToolInvocationFailure(string pluginName, string funcName, Exception ex)
    {
        var safePlugin = string.IsNullOrEmpty(pluginName) ? "?" : pluginName;
        var safeFunc = string.IsNullOrEmpty(funcName) ? "?" : funcName;
        var prefix = $"[工具调用失败] {safePlugin}.{safeFunc}: ";
        var detail = Truncate(ex.Message, MaxDetailChars);

        switch (ex)
        {
            case ArgumentNullException:
            case ArgumentException:
            case InvalidOperationException:
                return prefix + detail;

            case IOException:
                var friendlyIo = ErrorMessageHelper.GetFriendlyMessage(ex);
                return prefix + friendlyIo + (string.IsNullOrEmpty(detail) ? "" : $"（详情：{detail}）");

            default:
                return prefix + ErrorMessageHelper.GetFriendlyMessage(ex);
        }
    }

    private static string Truncate(string? message, int maxChars)
    {
        if (string.IsNullOrEmpty(message)) return "";
        if (message.Length <= maxChars) return message;
        return message[..maxChars] + "…";
    }
}
