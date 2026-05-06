using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>
/// 透传下游 SSE 字节流，同时从 <c>data:</c> 行中解析 <c>choices[0].delta.reasoning_content</c>（百炼 OpenAI 兼容）。
/// </summary>
internal sealed class DashScopeSseReasoningTapStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<string> _onReasoning;
    private readonly Action<string>? _onSseJsonLine;
    private readonly DashScopeSseTapTelemetry? _telemetry;
    private readonly List<byte> _lineBuf = new(512);
    private bool _disposed;

    /// <param name="onSseJsonLine">每条 <c>data:</c> JSON 负载（不含 <c>[DONE]</c>）解析前回调；供 OpenAI 兼容链检测 tool_calls 等。</param>
    public DashScopeSseReasoningTapStream(
        Stream inner,
        Action<string> onReasoning,
        DashScopeSseTapTelemetry? telemetry = null,
        Action<string>? onSseJsonLine = null)
    {
        _inner = inner;
        _onReasoning = onReasoning;
        _telemetry = telemetry;
        _onSseJsonLine = onSseJsonLine;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        if (n > 0)
            Feed(buffer[..n]);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (n > 0)
            Feed(buffer.Span[..n]);
        return n;
    }

    private void Feed(ReadOnlySpan<byte> chunk)
    {
        foreach (var b in chunk)
        {
            if (b == (byte)'\n')
            {
                ProcessLine();
                _lineBuf.Clear();
            }
            else
                _lineBuf.Add(b);
        }
    }

    private void ProcessLine()
    {
        while (_lineBuf.Count > 0 && _lineBuf[0] == (byte)'\r')
            _lineBuf.RemoveAt(0);
        if (_lineBuf.Count == 0)
            return;
        if (!LineStartsWithDataColon())
            return;
        var span = CollectionsMarshal.AsSpan(_lineBuf);
        var i = 5;
        while (i < span.Length && span[i] == (byte)' ')
            i++;
        if (i >= span.Length)
            return;
        var payload = span[i..];
        if (payload.SequenceEqual("[DONE]"u8))
            return;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(payload);
        }
        catch
        {
            return;
        }

        if (_telemetry != null)
        {
            _telemetry.SseDataLines++;
            if (_telemetry.SsePayloadPreviews.Count < 6)
            {
                var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
                var forLog = DashScopeChatRequestDiagnostics.FormatSseJsonPayloadForLog(oneLine);
                _telemetry.SsePayloadPreviews.Add(
                    DashScopeChatRequestDiagnostics.HeadTailOmitMiddle(
                        forLog,
                        DashScopeChatRequestDiagnostics.LogPreviewHeadChars,
                        DashScopeChatRequestDiagnostics.LogPreviewTailChars));
            }
        }

        _onSseJsonLine?.Invoke(text);
        TryExtractReasoningContent(text, _onReasoning, _telemetry);
    }

    private bool LineStartsWithDataColon()
    {
        var s = "data:"u8;
        if (_lineBuf.Count < s.Length)
            return false;
        var span = CollectionsMarshal.AsSpan(_lineBuf);
        return span.StartsWith(s);
    }

    /// <summary>百炼 / 部分 OpenAI 兼容实现可能使用不同大小写或字段名。</summary>
    private static readonly string[] DeltaReasoningPropertyNames =
    [
        "reasoning_content",
        "ReasoningContent",
        "reasoning",
    ];

    internal static void TryExtractReasoningContent(string jsonLine, Action<string> emit, DashScopeSseTapTelemetry? tel = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return;
            if (tel != null)
                tel.ChoiceChunksSeen++;
            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta))
                EmitReasoningFromObject(delta, emit, tel);
            // 少数 chunk 仅在 message 上带推理字段
            if (first.TryGetProperty("message", out var message))
                EmitReasoningFromObject(message, emit, tel);
        }
        catch (JsonException)
        {
            if (tel != null)
                tel.JsonParseErrors++;
        }
    }

    private static void EmitReasoningFromObject(JsonElement obj, Action<string> emit, DashScopeSseTapTelemetry? tel)
    {
        foreach (var name in DeltaReasoningPropertyNames)
        {
            if (!obj.TryGetProperty(name, out var rc))
                continue;
            if (rc.ValueKind != JsonValueKind.String)
                continue;
            var s = rc.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                if (tel != null)
                    tel.ReasoningFragmentsParsed++;
                emit(s);
                return;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
