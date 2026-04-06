using System.ComponentModel;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>内置语音转文字能力（百炼实时 ASR 文件通道），以 MCP_STT 注册 transcribe_audio。</summary>
public sealed class SttPlugin
{
    private readonly ITranscribeService _transcribeService;
    private readonly ILogger<SttPlugin> _logger;

    private const long MaxAudioBytes = 25 * 1024 * 1024; // 25 MB

    public SttPlugin(ITranscribeService transcribeService, ILogger<SttPlugin> logger)
    {
        _transcribeService = transcribeService;
        _logger = logger;
    }

    [ToolFunction("transcribe_audio")]
    [Description("Transcribe an audio file at the given local path to text using Alibaba DashScope real-time ASR (v1/inference). Requires realtime ASR API key in settings. Use when the user asks to convert speech/audio to text. Path must be accessible from this machine.")]
    public async Task<string> TranscribeAudioAsync(
        [Description("Full local path to the audio file (e.g. C:\\recordings\\meeting.mp3)")] string filePath,
        [Description("Optional language code for recognition, e.g. zh, en; leave empty for auto-detect")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "失败：请提供音频文件路径。";
        var path = filePath.Trim();
        if (!File.Exists(path))
        {
            _logger.LogWarning("[MCP_STT] transcribe_audio file not found: {Path}", path);
            return "失败：文件不存在或路径不可访问（" + path + "）。";
        }
        var fi = new FileInfo(path);
        if (fi.Length > MaxAudioBytes)
            return "失败：单文件超过 25MB 限制，请使用更短的音频或先分片。";
        if (fi.Length == 0)
            return "失败：文件为空。";
        string? contentType = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".mp3") contentType = "audio/mpeg";
        else if (ext == ".wav") contentType = "audio/wav";
        else if (ext == ".m4a") contentType = "audio/mp4";
        else if (ext == ".webm") contentType = "audio/webm";
        try
        {
            await using var stream = File.OpenRead(path);
            var text = await _transcribeService.TranscribeAsync(stream, contentType, string.IsNullOrWhiteSpace(language) ? null : language, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(text) ? "（未识别到文字）" : text;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("[MCP_STT] transcribe_audio: {Message}", ex.Message);
            return "失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MCP_STT] transcribe_audio failed for {Path}", path);
            return "失败：语音转写出错（" + ex.Message + "）。";
        }
    }
}
