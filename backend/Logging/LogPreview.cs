namespace OpenWorkmate.Server.Logging;

/// <summary>日志里展示长字符串时的统一截断（单行化、头尾、带省略字数）。</summary>
public static class LogPreview
{
    /// <summary>WebSocket 推理流结束日志：头/尾缓冲长度（码元），与 <see cref="ReasoningStreamLogState"/> 一致。</summary>
    public const int ReasoningStreamLogHeadChars = 96;
    public const int ReasoningStreamLogTailChars = 96;

    /// <summary>
    /// 常数空间累积推理正文，供流结束打一条预览；整段 <c>content</c> 参与尾部滑动窗（与计划一致）。
    /// </summary>
    public struct ReasoningStreamLogState
    {
        public int TotalChars { get; private set; }
        public string Head { get; private set; }
        public string Tail { get; private set; }

        public void AppendChunk(string? content)
        {
            if (string.IsNullOrEmpty(content)) return;
            var head = Head ?? "";
            var tail = Tail ?? "";
            TotalChars += content.Length;
            if (head.Length < ReasoningStreamLogHeadChars)
            {
                var need = ReasoningStreamLogHeadChars - head.Length;
                var take = Math.Min(need, content.Length);
                head += content.Substring(0, take);
            }

            var combined = tail + content;
            tail = combined.Length <= ReasoningStreamLogTailChars
                ? combined
                : combined.Substring(combined.Length - ReasoningStreamLogTailChars);
            Head = head;
            Tail = tail;
        }

        /// <summary>与整串 <see cref="HeadTail"/> 等价预览；<see cref="TotalChars"/>≤0 时返回空串。</summary>
        public readonly string BuildPreview()
        {
            if (TotalChars <= 0) return "";
            var h = Head ?? "";
            var t = Tail ?? "";
            var threshold = ReasoningStreamLogHeadChars + ReasoningStreamLogTailChars;
            string preview;
            if (TotalChars <= threshold)
            {
                var overlap = Math.Max(0, h.Length + t.Length - TotalChars);
                preview = h + t.Substring(overlap);
            }
            else
                preview = h + "…" + t;
            return SingleLine(preview);
        }
    }

    public static string SingleLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>单行化后取头尾，中间用「…」；短串原样返回。</summary>
    public static string HeadTail(string? s, int headChars = 32, int tailChars = 32)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = SingleLine(s);
        if (t.Length <= headChars + tailChars) return t;
        return t.Substring(0, headChars) + "…" + t.Substring(t.Length - tailChars);
    }

    /// <summary>头尾 + 中间省略字数（适合 HTTP/JSON 错误体等大段文本）。</summary>
    public static string HeadTailOmitMiddle(string? s, int headChars, int tailChars)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= headChars + tailChars) return s;
        var omitted = s.Length - headChars - tailChars;
        return s.Substring(0, headChars)
            + " …[omitted "
            + omitted
            + " chars]… "
            + s.Substring(s.Length - tailChars);
    }
}
