namespace OfficeCopilot.Server.Services;

/// <summary>OCR 持久化 connectionKind 常量；与前端 options.js 保持一致。</summary>
public static class ModelConnectionKind
{
    public static class Ocr
    {
        public const string OpenAiCompatibleMultipart = "openai_compatible_multipart";
        public const string DashScopeOpenAiChatImage = "dashscope_openai_chat_image";
    }

    public static bool IsValidOcr(string? value)
    {
        var v = (value ?? "").Trim();
        if (string.IsNullOrEmpty(v)) return true;
        return string.Equals(v, Ocr.OpenAiCompatibleMultipart, StringComparison.Ordinal)
               || string.Equals(v, Ocr.DashScopeOpenAiChatImage, StringComparison.Ordinal);
    }
}
