namespace OfficeCopilot.Server.Services;

/// <summary>vendorId → connectionKind；空 vendorId 表示走 URL 自动识别。</summary>
public static class VendorModelDefaults
{
    /// <summary>返回 null 表示不覆盖，使用 endpoint 启发式。</summary>
    public static string? ResolveSttConnectionKindFromVendor(string? vendorId)
    {
        var v = (vendorId ?? "").Trim();
        if (string.IsNullOrEmpty(v) || string.Equals(v, AiVendorIds.OtherAuto, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(v, AiVendorIds.AliyunBailian, StringComparison.OrdinalIgnoreCase))
            return ModelConnectionKind.Stt.DashScopeOpenAiChatAudio;
        return ModelConnectionKind.Stt.OpenAiWhisperMultipart;
    }

    public static string? ResolveOcrConnectionKindFromVendor(string? vendorId)
    {
        var v = (vendorId ?? "").Trim();
        if (string.IsNullOrEmpty(v) || string.Equals(v, AiVendorIds.OtherAuto, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(v, AiVendorIds.AliyunBailian, StringComparison.OrdinalIgnoreCase))
            return ModelConnectionKind.Ocr.DashScopeOpenAiChatImage;
        return ModelConnectionKind.Ocr.OpenAiCompatibleMultipart;
    }
}
