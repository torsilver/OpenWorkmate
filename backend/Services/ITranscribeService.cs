namespace OfficeCopilot.Server.Services;

/// <summary>语音转文字（Whisper）服务，供 POST /api/transcribe 与内置工具 transcribe_audio 复用。</summary>
public interface ITranscribeService
{
    /// <summary>将音频流转写为文本。失败时抛出异常，消息可供用户展示。</summary>
    /// <param name="audioStream">音频数据流。</param>
    /// <param name="contentType">可选 MIME 类型，如 audio/mpeg。</param>
    /// <param name="language">可选语言代码，如 zh、en；空则自动检测。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>识别出的文本。</returns>
    Task<string> TranscribeAsync(Stream audioStream, string? contentType, string? language, CancellationToken ct = default);
}
