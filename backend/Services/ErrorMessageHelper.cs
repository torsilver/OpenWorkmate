namespace OpenWorkmate.Server.Services;

/// <summary>
/// 将异常或错误码映射为面向用户的友好中文文案。
/// 覆盖常见 HTTP 状态码、OpenAI/兼容接口错误及本地文件/权限错误。
/// </summary>
public static class ErrorMessageHelper
{
    public static string GetFriendlyMessage(Exception ex)
    {
        if (ex == null) return "操作失败，请稍后重试。";

        var msg = (ex.Message ?? "").ToLowerInvariant();
        var full = (ex.ToString() ?? "").ToLowerInvariant();

        // ---------- 认证/授权 ----------
        // 401：API 密钥无效或过期
        if (msg.Contains("401") || msg.Contains("unauthorized") || msg.Contains("invalid api key")
            || msg.Contains("invalid_api_key") || msg.Contains("incorrect api key") || msg.Contains("incorrect_api_key")
            || msg.Contains("authentication") || full.Contains("401"))
            return "API 密钥无效或已过期，请到设置页检查并更新。";

        // 403：无权限、配额或区域限制
        if (msg.Contains("403") || msg.Contains("forbidden") || msg.Contains("quota") || msg.Contains("region")
            || msg.Contains("access_denied") || msg.Contains("resource_not_available"))
            return "无访问权限或配额已用尽，请检查 API 权限与用量；若使用区域化服务请确认区域可用。";

        // ---------- 客户端请求错误 ----------
        // 404：接口或模型不存在（AI 相关优先于通用「未找到」）
        if (msg.Contains("404") || (msg.Contains("not found") && (full.Contains("service request") || full.Contains("clientresult") || full.Contains("openai") || full.Contains("chat"))))
            return "AI 接口返回 404，请到设置页检查：1) 接口地址是否需加 /v1 后缀；2) 模型 ID 是否在该服务中存在。";

        // 408 / 超时
        if (ex is TimeoutException || msg.Contains("timeout") || msg.Contains("timed out") || msg.Contains("408"))
            return "请求超时，请检查网络后重试。";

        // 429：速率限制
        if (msg.Contains("429") || msg.Contains("rate limit") || msg.Contains("too many requests")
            || msg.Contains("rate_limit_exceeded"))
            return "请求过于频繁，请稍后再试。";

        // platform_upstream_error：上游平台无法完成推理（常见于多轮对话或带工具调用的第二轮请求）
        if (msg.Contains("platform_upstream_error") || msg.Contains("platform could not complete"))
            return "AI 上游平台无法完成本次推理（常见于多轮对话或工具调用后的请求）。建议：1) 新开对话重试；2) 在设置中调小「历史轮数」再试；3) 若同一 key/地址在其他软件正常，可能是本端发送的请求格式或长度与当前网关不兼容，可尝试换模型或接口。";

        // 400 / 错误请求：参数错误、模型不存在等
        if (msg.Contains("400") || full.Contains("invalid_request_error") || msg.Contains("invalid_model")
            || msg.Contains("model_not_found") || msg.Contains("deployment_not_found"))
            return "AI 请求被拒绝（参数或模型错误）。请到设置页确认接口地址与模型 ID（或 Azure 部署名）是否正确，然后重试。";

        // 内容/安全策略（如 OpenAI content_filter）
        if (msg.Contains("content_filter") || msg.Contains("content policy") || msg.Contains("blocked")
            || msg.Contains("safety") || msg.Contains("policy violation"))
            return "请求或回复因内容策略被拒绝，请修改输入后重试。";

        // 上下文超长
        if (msg.Contains("context_length") || msg.Contains("maximum context") || msg.Contains("token limit")
            || msg.Contains("too many tokens"))
            return "对话或输入过长，超出模型限制。请缩短内容或新开对话后重试。";

        // ---------- 服务端错误 ----------
        if (msg.Contains("500") || msg.Contains("502") || msg.Contains("503") || msg.Contains("504")
            || msg.Contains("server error") || msg.Contains("internal server error")
            || msg.Contains("service unavailable") || msg.Contains("bad gateway") || msg.Contains("gateway timeout"))
            return "模型服务暂时不可用，请稍后重试。";

        // ---------- 网络/连接 ----------
        if (msg.Contains("connection refused") || msg.Contains("connection reset") || msg.Contains("econnrefused")
            || msg.Contains("network is unreachable") || msg.Contains("no route to host")
            || msg.Contains("could not resolve host") || msg.Contains("dns") || msg.Contains("name resolution"))
            return "无法连接至服务，请检查网络与接口地址是否正确。";

        if (msg.Contains("ssl") || msg.Contains("certificate") || msg.Contains("tls") || msg.Contains("https"))
            return "安全连接失败，请检查 HTTPS/证书或代理设置。";

        // ---------- 本地文件/权限 ----------
        if (msg.Contains("正被另一进程使用") || msg.Contains("being used by another process")
            || msg.Contains("文件正在使用") || msg.Contains("sharing violation") || msg.Contains("locked"))
            return "文件正被其他程序占用，请关闭后重试。";

        if (ex is FileNotFoundException || full.Contains("filenotfound"))
            return "未找到指定文件，请检查路径是否正确。";

        if (msg.Contains("not found") || msg.Contains("找不到") || msg.Contains("does not exist"))
            return "未找到指定资源，请检查路径或配置。";

        if (ex is UnauthorizedAccessException || msg.Contains("access denied") || msg.Contains("拒绝访问")
            || msg.Contains("permission denied"))
            return "没有访问该文件或目录的权限。";

        // ---------- 默认 ----------
        return "操作失败，请稍后重试。若持续出现，请查看日志或联系支持。";
    }
}
