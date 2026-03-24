using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.Stt;

namespace OfficeCopilot.Server.Services;

/// <summary>语音转文字：委托 <see cref="SttTranscriberProvider"/>，支持 connectionKind / vendorId 与 URL 自动识别。</summary>
public sealed class TranscribeService : ITranscribeService
{
    private readonly ConfigService _configService;
    private readonly SttTranscriberProvider _sttTranscriberProvider;

    public TranscribeService(ConfigService configService, SttTranscriberProvider sttTranscriberProvider)
    {
        _configService = configService;
        _sttTranscriberProvider = sttTranscriberProvider;
    }

    public Task<string> TranscribeAsync(Stream audioStream, string? contentType, string? language, CancellationToken ct = default)
    {
        var entry = _configService.GetActiveSttEntry();
        if (entry == null || string.IsNullOrWhiteSpace(entry.Endpoint) || string.IsNullOrWhiteSpace(entry.ApiKey))
            throw new InvalidOperationException("未配置语音转文字。请在设置中配置 STT 模型的 endpoint 与 API Key。");
        return _sttTranscriberProvider.TranscribeAsync(audioStream, contentType, language, entry, ct);
    }
}
