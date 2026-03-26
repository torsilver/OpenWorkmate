using OfficeCopilot.Server.Services.Stt;

namespace OfficeCopilot.Server.Services;

/// <summary>语音转文字：仅支持已配置百炼实时识别（v1/inference WebSocket 文件流）。</summary>
public sealed class TranscribeService : ITranscribeService
{
    private readonly ConfigService _configService;
    private readonly DashScopeInferenceFileTranscriber _dashScopeFile;

    public TranscribeService(ConfigService configService, DashScopeInferenceFileTranscriber dashScopeFile)
    {
        _configService = configService;
        _dashScopeFile = dashScopeFile;
    }

    public async Task<string> TranscribeAsync(Stream audioStream, string? contentType, string? language, CancellationToken ct = default)
    {
        var ra = _configService.Current.RealtimeAsr;
        if (ra == null || string.IsNullOrWhiteSpace(ra.ApiKey))
        {
            throw new InvalidOperationException(
                "未配置百炼实时语音识别 API Key。语音输入、会议监听与文件转写（含 MCP_STT、POST /api/transcribe）均依赖此项；请在设置「百炼实时语音识别」中填写有效的 API Key。");
        }

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await audioStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        return await _dashScopeFile.TranscribeAsync(bytes, contentType, language, ra, ct).ConfigureAwait(false);
    }
}
