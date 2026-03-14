namespace OfficeCopilot.Server.Services.Memory;

/// <summary>简单分块：按段落/句子切分，尽量不超过 maxChars 字符。</summary>
public static class TextChunker
{
    private const int DefaultMaxChunkChars = 800;
    private const int OverlapChars = 50;

    /// <summary>将文本切分为多个块，每块约 maxChunkChars 字符，重叠 overlapChars。</summary>
    public static IReadOnlyList<string> Chunk(string text, int maxChunkChars = DefaultMaxChunkChars, int overlapChars = OverlapChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        text = text.Trim();
        if (text.Length <= maxChunkChars) return new[] { text };

        var chunks = new List<string>();
        var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        foreach (var p in paragraphs)
        {
            var trimmed = p.Trim();
            if (trimmed.Length == 0) continue;
            if (current.Length + trimmed.Length + 2 > maxChunkChars && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                var lastPart = current.ToString();
                current.Clear();
                if (overlapChars > 0 && lastPart.Length > overlapChars)
                {
                    var overlap = lastPart.AsSpan(lastPart.Length - overlapChars);
                    current.Append(overlap);
                }
            }
            if (current.Length > 0) current.Append("\n\n");
            current.Append(trimmed);
        }
        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());
        return chunks;
    }
}
