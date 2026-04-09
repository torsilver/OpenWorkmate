namespace OfficeCopilot.Server.Logging;

/// <summary>日志里展示长字符串时的统一截断（单行化、头尾、带省略字数）。</summary>
public static class LogPreview
{
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
