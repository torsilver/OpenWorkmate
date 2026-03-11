namespace OfficeCopilot.Server.Services;

/// <summary>
/// 将异常或错误码映射为面向用户的友好中文文案。
/// </summary>
public static class ErrorMessageHelper
{
    public static string GetFriendlyMessage(Exception ex)
    {
        if (ex == null) return "操作失败，请稍后重试。";

        var msg = (ex.Message ?? "").ToLowerInvariant();
        var full = (ex.ToString() ?? "").ToLowerInvariant();

        // API 密钥无效/过期（含 OpenAI/兼容接口返回的 incorrect api key）
        if (msg.Contains("401") || msg.Contains("unauthorized") || msg.Contains("invalid api key")
            || msg.Contains("invalid_api_key") || msg.Contains("incorrect api key") || msg.Contains("incorrect_api_key")
            || msg.Contains("authentication") || full.Contains("401"))
            return "API 密钥无效或已过期，请到设置页检查并更新。";

        // 网络/请求超时
        if (ex is TimeoutException || msg.Contains("timeout") || msg.Contains("timed out"))
            return "请求超时，请检查网络后重试。";

        // 文件被占用
        if (msg.Contains("正被另一进程使用") || msg.Contains("being used by another process")
            || msg.Contains("文件正在使用") || msg.Contains("sharing violation") || msg.Contains("locked"))
            return "文件正被其他程序占用，请关闭后重试。";

        // API 404：接口或模型不存在（优先于通用「未找到」）
        if (msg.Contains("404") || (msg.Contains("not found") && (full.Contains("service request") || full.Contains("clientresult") || full.Contains("openai") || full.Contains("chat"))))
            return "AI 接口返回 404，请到设置页检查：1) 接口地址是否需加 /v1 后缀（如 http://地址:端口/v1）；2) 模型 ID 是否在该服务中存在。";

        // 文件未找到
        if (ex is FileNotFoundException || msg.Contains("not found") || msg.Contains("找不到")
            || msg.Contains("does not exist") || full.Contains("filenotfound"))
            return "未找到指定文件，请检查路径是否正确。";

        // 无权限
        if (ex is UnauthorizedAccessException || msg.Contains("access denied") || msg.Contains("拒绝访问")
            || msg.Contains("permission denied"))
            return "没有访问该文件或目录的权限。";

        // 模型/服务错误 (5xx)
        if (msg.Contains("502") || msg.Contains("503") || msg.Contains("504") || msg.Contains("server error")
            || msg.Contains("internal server error") || msg.Contains("service unavailable"))
            return "模型服务暂时不可用，请稍后重试。";

        // 速率限制
        if (msg.Contains("429") || msg.Contains("rate limit") || msg.Contains("too many requests"))
            return "请求过于频繁，请稍后再试。";

        // 默认
        return "操作失败，请稍后重试。若持续出现，请查看日志或联系支持。";
    }
}
