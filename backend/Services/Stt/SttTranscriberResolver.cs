using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.Stt;

public static class SttTranscriberResolver
{
    public static SttUpstreamAdapter.UpstreamKind Resolve(SttModelEntry entry) =>
        Resolve(entry.Endpoint, entry.ConnectionKind, entry.VendorId);

    /// <summary>解析顺序：非空 connectionKind → vendorId 映射 → URL 启发式。</summary>
    public static SttUpstreamAdapter.UpstreamKind Resolve(string endpoint, string? connectionKind, string? vendorId)
    {
        var ck = (connectionKind ?? "").Trim();
        if (string.IsNullOrEmpty(ck))
            ck = VendorModelDefaults.ResolveSttConnectionKindFromVendor(vendorId) ?? "";

        if (!string.IsNullOrEmpty(ck))
        {
            if (string.Equals(ck, ModelConnectionKind.Stt.DashScopeOpenAiChatAudio, StringComparison.Ordinal))
                return SttUpstreamAdapter.UpstreamKind.DashScopeQwenOpenAICompatible;
            if (string.Equals(ck, ModelConnectionKind.Stt.OpenAiWhisperMultipart, StringComparison.Ordinal))
                return SttUpstreamAdapter.UpstreamKind.WhisperCompatible;
            throw new InvalidOperationException("不支持的 STT connectionKind: " + ck + "。有效取值为空（自动）、openai_whisper_multipart、dashscope_openai_chat_audio。");
        }

        return SttUpstreamAdapter.ResolveMode(endpoint);
    }
}
