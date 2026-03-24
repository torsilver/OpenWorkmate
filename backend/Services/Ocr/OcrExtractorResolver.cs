using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Ocr;

public enum OcrBackendKind
{
    OpenAiMultipart,
    DashScopeOpenAiChatImage
}

public static class OcrExtractorResolver
{
    public static OcrBackendKind Resolve(OcrModelEntry entry) =>
        Resolve(entry.Endpoint, entry.ConnectionKind, entry.VendorId);

    public static OcrBackendKind Resolve(string endpoint, string? connectionKind, string? vendorId)
    {
        var ck = (connectionKind ?? "").Trim();
        if (string.IsNullOrEmpty(ck))
            ck = VendorModelDefaults.ResolveOcrConnectionKindFromVendor(vendorId) ?? "";

        if (!string.IsNullOrEmpty(ck))
        {
            if (string.Equals(ck, ModelConnectionKind.Ocr.DashScopeOpenAiChatImage, StringComparison.Ordinal))
                return OcrBackendKind.DashScopeOpenAiChatImage;
            if (string.Equals(ck, ModelConnectionKind.Ocr.OpenAiCompatibleMultipart, StringComparison.Ordinal))
                return OcrBackendKind.OpenAiMultipart;
            throw new InvalidOperationException("不支持的 OCR connectionKind: " + ck + "。有效取值为空（自动）、openai_compatible_multipart、dashscope_openai_chat_image。");
        }

        var ep = (endpoint ?? "").Trim();
        if (ep.Contains("compatible-mode", StringComparison.OrdinalIgnoreCase))
            return OcrBackendKind.DashScopeOpenAiChatImage;
        return OcrBackendKind.OpenAiMultipart;
    }
}
