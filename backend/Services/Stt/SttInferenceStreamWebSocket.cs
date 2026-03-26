using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OfficeCopilot.Server.Services.Stt;

/// <summary>
/// 扩展 ↔ 本机 WebSocket ↔ 百炼 v1/inference；二进制帧为 PCM s16le 单声道，采样率与 run-task 一致。
/// </summary>
public sealed class SttInferenceStreamWebSocket
{
    private readonly IMeetingTranscriptStore _meetingStore;
    private readonly ILogger<SttInferenceStreamWebSocket> _logger;

    public SttInferenceStreamWebSocket(IMeetingTranscriptStore meetingStore, ILogger<SttInferenceStreamWebSocket> logger)
    {
        _meetingStore = meetingStore;
        _logger = logger;
    }

    public async Task HandleAsync(
        WebSocket browserWs,
        RealtimeAsrConfig? cfg,
        string mode,
        string? meetingSessionId,
        CancellationToken ct)
    {
        if (cfg == null)
        {
            await SendBrowserJsonAsync(browserWs, new { type = "error", message = "未配置百炼实时语音识别（realtimeAsr）。" }, ct).ConfigureAwait(false);
            return;
        }

        var apiKey = (cfg.ApiKey ?? "").Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            await SendBrowserJsonAsync(browserWs, new { type = "error", message = "未配置百炼实时语音识别 API Key。" }, ct).ConfigureAwait(false);
            return;
        }

        var meeting = string.Equals(mode, "meeting", StringComparison.OrdinalIgnoreCase);
        var safeMeetingId = meeting ? SanitizeMeetingSessionId(meetingSessionId) : null;
        if (meeting && string.IsNullOrEmpty(safeMeetingId))
        {
            await SendBrowserJsonAsync(browserWs, new { type = "error", message = "会议模式需要有效的 meetingSessionId 查询参数。" }, ct).ConfigureAwait(false);
            return;
        }

        var modelId = string.IsNullOrWhiteSpace(cfg.ModelId) ? "fun-asr-realtime" : cfg.ModelId.Trim();
        var sampleRate = DashScopeInferenceAsrProtocol.DefaultSampleRateForModel(modelId);
        var wsUrl = string.IsNullOrWhiteSpace(cfg.WebSocketBaseUrl)
            ? "wss://dashscope.aliyuncs.com/api-ws/v1/inference"
            : cfg.WebSocketBaseUrl.Trim();

        if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "wss" && uri.Scheme != "ws"))
        {
            await SendBrowserJsonAsync(browserWs, new { type = "error", message = "百炼 WebSocket 地址无效。" }, ct).ConfigureAwait(false);
            return;
        }

        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        var wsId = (cfg.WorkspaceId ?? "").Trim();
        if (wsId.Length > 0)
            upstream.Options.SetRequestHeader("X-DashScope-WorkSpace", wsId);

        try
        {
            await upstream.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SttInference upstream connect failed");
            await SendBrowserJsonAsync(browserWs, new { type = "error", message = "连接百炼实时语音识别失败: " + ex.Message }, ct).ConfigureAwait(false);
            return;
        }

        var taskId = Guid.NewGuid().ToString("N");
        var runJson = DashScopeInferenceAsrProtocol.BuildRunTaskJson(taskId, cfg, sampleRate, "pcm", meetingMode: meeting);
        try
        {
            await upstream.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(runJson)), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SttInference run-task send failed");
            await SendBrowserJsonAsync(browserWs, new { type = "error", message = "发送 run-task 失败: " + ex.Message }, ct).ConfigureAwait(false);
            try
            {
                await upstream.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "error", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                /* ignored */
            }

            return;
        }

        var nextSegmentSeq = 0;
        var finishSent = false;
        var upstreamBuffer = new byte[256 * 1024];
        var pending = new List<byte>();
        var taskStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReadingUpstream = new CancellationTokenSource();
        var linkedUpstream = CancellationTokenSource.CreateLinkedTokenSource(ct, stopReadingUpstream.Token);

        async Task TrySendFinishOnceAsync(CancellationToken c)
        {
            if (finishSent)
                return;
            finishSent = true;
            await SendFinishAndDrainAsync(upstream, taskId, c).ConfigureAwait(false);
        }

        async Task UpstreamReadLoop()
        {
            try
            {
                while (upstream.State == WebSocketState.Open && !linkedUpstream.Token.IsCancellationRequested)
                {
                    var r = await upstream.ReceiveAsync(new ArraySegment<byte>(upstreamBuffer), linkedUpstream.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    if (r.MessageType != WebSocketMessageType.Text)
                        continue;
                    pending.AddRange(upstreamBuffer.AsSpan(0, r.Count).ToArray());
                    if (!r.EndOfMessage)
                        continue;
                    var json = Encoding.UTF8.GetString(pending.ToArray());
                    pending.Clear();

                    if (!DashScopeInferenceAsrProtocol.TryParseUpstreamEvent(json, out var ev, out var err, out var st, out var sentEnd, out var hbSkip))
                        continue;

                    if (string.Equals(ev, "task-failed", StringComparison.Ordinal))
                    {
                        await SendBrowserJsonAsync(browserWs,
                            new { type = "error", message = string.IsNullOrEmpty(err) ? "百炼任务失败。" : err },
                            ct).ConfigureAwait(false);
                        taskStarted.TrySetResult(false);
                        break;
                    }

                    if (string.Equals(ev, "task-started", StringComparison.Ordinal))
                        taskStarted.TrySetResult(true);

                    if (string.Equals(ev, "task-finished", StringComparison.Ordinal))
                        break;

                    if (string.Equals(ev, "result-generated", StringComparison.Ordinal) && !hbSkip && !string.IsNullOrWhiteSpace(st))
                    {
                        var text = st.Trim();
                        if (sentEnd)
                        {
                            var seq = nextSegmentSeq;
                            if (meeting && !string.IsNullOrEmpty(safeMeetingId))
                            {
                                try
                                {
                                    await _meetingStore.AppendSegmentAsync(safeMeetingId, seq, text, ct).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Meeting transcript append failed");
                                    await SendBrowserJsonAsync(browserWs, new { type = "error", message = "实录落盘失败: " + ex.Message }, ct)
                                        .ConfigureAwait(false);
                                }
                            }

                            nextSegmentSeq++;
                            await SendBrowserJsonAsync(browserWs, new { type = "final", text, sequence = seq }, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await SendBrowserJsonAsync(browserWs, new { type = "partial", text }, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                /* normal */
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SttInference upstream read loop");
                await SendBrowserJsonAsync(browserWs, new { type = "error", message = "上游连接异常: " + ex.Message }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        var upstreamTask = Task.Run(() => UpstreamReadLoop(), CancellationToken.None);

        try
        {
            var ok = await Task.WhenAny(taskStarted.Task, Task.Delay(15000, ct)).ConfigureAwait(false) == taskStarted.Task
                     && await taskStarted.Task.ConfigureAwait(false);
            if (!ok)
            {
                await SendBrowserJsonAsync(browserWs, new { type = "error", message = "等待百炼 task-started 超时或未成功。" }, ct)
                    .ConfigureAwait(false);
                stopReadingUpstream.Cancel();
                await SafeClosePair(browserWs, upstream, upstreamTask).ConfigureAwait(false);
                return;
            }

            await SendBrowserJsonAsync(browserWs, new { type = "ready", sampleRate }, ct).ConfigureAwait(false);

            var browserBuffer = new byte[128 * 1024];
            while (browserWs.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var br = await browserWs.ReceiveAsync(new ArraySegment<byte>(browserBuffer), ct).ConfigureAwait(false);
                if (br.MessageType == WebSocketMessageType.Close)
                    break;
                if (br.MessageType == WebSocketMessageType.Text)
                {
                    var txt = Encoding.UTF8.GetString(browserBuffer, 0, br.Count);
                    if (TryParseStop(txt))
                    {
                        await TrySendFinishOnceAsync(ct).ConfigureAwait(false);
                        break;
                    }
                }
                else if (br.MessageType == WebSocketMessageType.Binary)
                {
                    if (upstream.State != WebSocketState.Open)
                        break;
                    await upstream.SendAsync(new ArraySegment<byte>(browserBuffer, 0, br.Count), WebSocketMessageType.Binary, br.EndOfMessage, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                await TrySendFinishOnceAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                /* ignored */
            }

            stopReadingUpstream.Cancel();
            await SafeClosePair(browserWs, upstream, upstreamTask).ConfigureAwait(false);
        }
    }

    private static bool TryParseStop(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                return false;
            return string.Equals(t.GetString(), "stop", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task SendFinishAndDrainAsync(ClientWebSocket upstream, string taskId, CancellationToken ct)
    {
        if (upstream.State != WebSocketState.Open)
            return;
        var finishJson = DashScopeInferenceAsrProtocol.BuildFinishTaskJson(taskId);
        try
        {
            await upstream.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(finishJson)), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            /* ignored */
        }
    }

    private static async Task SafeClosePair(WebSocket browserWs, ClientWebSocket upstream, Task upstreamTask)
    {
        try
        {
            if (upstream.State == WebSocketState.Open)
                await upstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            /* ignored */
        }

        try
        {
            await upstreamTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            /* ignored */
        }

        try
        {
            if (browserWs.State == WebSocketState.Open)
                await browserWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            /* ignored */
        }
    }

    private static async Task SendBrowserJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
            return;
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static string? SanitizeMeetingSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var s = sessionId.Trim();
        if (s.Length > 80) s = s[..80];
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') continue;
            return null;
        }

        return s.Length > 0 ? s : null;
    }
}
