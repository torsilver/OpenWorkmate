namespace OfficeCopilot.Server.Services;

/// <summary>
/// 文本类文件工具参数与截断（无 IO，便于单测）。字符上限与 PDF 读文本一致。
/// </summary>
public static class TextFileToolNormalize
{
    /// <summary>读取前按字节限制，避免一次性载入过大文件（8 MiB）。</summary>
    public const long MaxBytesToRead = 8 * 1024 * 1024;

    public const string TruncationSuffix = "\n\n[已截断：超出 maxChars 限制。可增大 maxChars 或分段读取（上限见工具说明）。]";

    /// <summary>将调用方传入的 maxChars 限制在合理范围；null 或非正数使用 <see cref="PdfToolNormalize.DefaultMaxChars"/>。</summary>
    public static int NormalizeMaxChars(int? maxChars) => PdfToolNormalize.NormalizeMaxChars(maxChars);

    /// <summary>若文本长度超过 <paramref name="maxChars"/>，截断并追加 <see cref="TruncationSuffix"/>。</summary>
    public static string ApplyMaxCharLimit(string text, int maxChars, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxChars)
        {
            truncated = false;
            return text;
        }

        truncated = true;
        return text[..maxChars] + TruncationSuffix;
    }
}
