namespace OpenWorkmate.Server.Services;

/// <summary>PDF 工具参数归一化（无 IO，便于单测）。</summary>
public static class PdfToolNormalize
{
    public const int DefaultMaxChars = 200_000;
    public const int AbsoluteMaxChars = 2_000_000;

    /// <summary>将调用方传入的 maxChars 限制在合理范围；null 或非正数使用 <see cref="DefaultMaxChars"/>。</summary>
    public static int NormalizeMaxChars(int? maxChars)
    {
        if (maxChars is null or <= 0)
            return DefaultMaxChars;
        return Math.Min(maxChars.Value, AbsoluteMaxChars);
    }

    /// <summary>将 1-based 页码区间限制在 [1, pageCount]；若 first &gt; last 则交换。pageCount 必须 ≥ 1。</summary>
    public static bool TryNormalizePageRange(int pageCount, int? firstPage, int? lastPage, out int from1Based, out int to1Based, out string? errorMessage)
    {
        from1Based = 0;
        to1Based = 0;
        errorMessage = null;
        if (pageCount < 1)
        {
            errorMessage = "失败：文档页数无效。";
            return false;
        }

        var f = firstPage ?? 1;
        var l = lastPage ?? pageCount;
        f = Math.Clamp(f, 1, pageCount);
        l = Math.Clamp(l, 1, pageCount);
        if (f > l)
            (f, l) = (l, f);
        from1Based = f;
        to1Based = l;
        return true;
    }
}
