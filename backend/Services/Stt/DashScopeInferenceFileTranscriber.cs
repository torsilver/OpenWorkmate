using System.Net.WebSockets;
using System.Text;

namespace OpenWorkmate.Server.Services.Stt;

/// <summary>
/// 通过百炼 <c>v1/inference</c> WebSocket 对整文件做识别（分块发送二进制音频），聚合句末结果。
/// </summary>
public sealed class DashScopeInferenceFileTranscriber
{
    private readonly ILogger<DashScopeInferenceFileTranscriber> _logger;

    public DashScopeInferenceFileTranscriber(ILogger<DashScopeInferenceFileTranscriber> logger)
    {
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(
        byte[] audioBytes,
        string? contentType,
        string? language,
        RealtimeAsrConfig cfg,
        CancellationToken ct = default)
    {
        if (audioBytes == null || audioBytes.Length == 0)
            throw new InvalidOperationException("音频数据为空。");
        var apiKey = (cfg.ApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("未配置百炼实时语音识别 API Key。请在设置「百炼实时语音识别」中填写。");

        var (format, sampleRate) = ResolveFormatAndRate(audioBytes, contentType, cfg);
        var cfgEffective = cfg;
        if (!string.IsNullOrWhiteSpace(language))
        {
            var lang = language.Trim().ToLowerInvariant();
            var hints = cfg.LanguageHints != null ? new List<string>(cfg.LanguageHints) : new List<string>();
            if (!hints.Contains(lang, StringComparer.OrdinalIgnoreCase))
                hints.Add(lang);
            cfgEffective = CloneCfg(cfg, hints);
        }

        var wsUrl = string.IsNullOrWhiteSpace(cfg.WebSocketBaseUrl)
            ? "wss://dashscope.aliyuncs.com/api-ws/v1/inference"
            : cfg.WebSocketBaseUrl.Trim();

        if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "wss" && uri.Scheme != "ws"))
            throw new InvalidOperationException("百炼实时语音识别 WebSocket 地址无效。");

        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        var wsId = (cfg.WorkspaceId ?? "").Trim();
        if (wsId.Length > 0)
            upstream.Options.SetRequestHeader("X-DashScope-WorkSpace", wsId);

        await upstream.ConnectAsync(uri, ct).ConfigureAwait(false);

        var taskId = Guid.NewGuid().ToString("N");
        var runJson = DashScopeInferenceAsrProtocol.BuildRunTaskJson(taskId, cfgEffective, sampleRate, format, meetingMode: false);
        await upstream.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(runJson)), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        var textBuilder = new List<string>();
        var receiveBuffer = new byte[256 * 1024];
        var pending = new List<byte>();

        async Task WaitForEventAsync(string expectedEvent, CancellationToken c)
        {
            while (upstream.State == WebSocketState.Open && !c.IsCancellationRequested)
            {
                var r = await upstream.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), c).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                    return;
                if (r.MessageType != WebSocketMessageType.Text)
                    continue;
                pending.AddRange(receiveBuffer.AsSpan(0, r.Count).ToArray());
                if (!r.EndOfMessage)
                    continue;
                var json = Encoding.UTF8.GetString(pending.ToArray());
                pending.Clear();
                if (!DashScopeInferenceAsrProtocol.TryParseUpstreamEvent(json, out var ev, out var err, out var st, out var sentEnd, out var hbSkip))
                    continue;
                if (string.Equals(ev, "task-failed", StringComparison.Ordinal))
                    throw new InvalidOperationException(string.IsNullOrEmpty(err) ? "百炼实时语音识别任务失败。" : err);
                if (string.Equals(ev, "result-generated", StringComparison.Ordinal) && !hbSkip && sentEnd && !string.IsNullOrWhiteSpace(st))
                    textBuilder.Add(st.Trim());
                if (string.Equals(ev, expectedEvent, StringComparison.Ordinal))
                    return;
            }
        }

        await WaitForEventAsync("task-started", ct).ConfigureAwait(false);

        const int chunk = 3200;
        for (var off = 0; off < audioBytes.Length; off += chunk)
        {
            var len = Math.Min(chunk, audioBytes.Length - off);
            await upstream.SendAsync(new ArraySegment<byte>(audioBytes, off, len), WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);
            await Task.Delay(80, ct).ConfigureAwait(false);
        }

        var finishJson = DashScopeInferenceAsrProtocol.BuildFinishTaskJson(taskId);
        await upstream.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(finishJson)), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        await WaitForEventAsync("task-finished", ct).ConfigureAwait(false);

        try
        {
            await upstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            /* ignored */
        }

        return string.Join("", textBuilder).Trim();
    }

    private static RealtimeAsrConfig CloneCfg(RealtimeAsrConfig src, List<string> hints) => new()
    {
        ApiKey = src.ApiKey,
        WebSocketBaseUrl = src.WebSocketBaseUrl,
        ModelId = src.ModelId,
        LanguageHints = hints,
        Heartbeat = src.Heartbeat,
        SemanticPunctuationEnabled = src.SemanticPunctuationEnabled,
        DisfluencyRemovalEnabled = src.DisfluencyRemovalEnabled,
        WorkspaceId = src.WorkspaceId
    };

    private static (string Format, int SampleRate) ResolveFormatAndRate(byte[] audioBytes, string? contentType, RealtimeAsrConfig cfg)
    {
        var ct = (contentType ?? "").ToLowerInvariant();
        var modelId = (cfg.ModelId ?? "").Trim();
        var defaultRate = DashScopeInferenceAsrProtocol.DefaultSampleRateForModel(modelId);

        if (ct.Contains("wav") || HasWavRiff(audioBytes))
        {
            if (TryParseWavPcm(audioBytes, out var rate))
                return ("wav", rate);
            return ("wav", defaultRate);
        }

        if (ct.Contains("mpeg") || ct.Contains("mp3"))
            return ("mp3", defaultRate);

        if (ct.Contains("webm"))
            throw new InvalidOperationException("WebM 格式当前不支持直接识别，请先转为 WAV 或 MP3 再试。");

        return ("pcm", defaultRate);
    }

    private static bool HasWavRiff(byte[] b) => b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F';

    private static bool TryParseWavPcm(byte[] wav, out int sampleRate)
    {
        sampleRate = 16000;
        try
        {
            if (wav.Length < 44 || !HasWavRiff(wav))
                return false;
            // offset 24: sample rate uint32 LE
            sampleRate = BitConverter.ToInt32(wav.AsSpan(24, 4));
            return sampleRate > 0 && sampleRate <= 192000;
        }
        catch
        {
            return false;
        }
    }
}
