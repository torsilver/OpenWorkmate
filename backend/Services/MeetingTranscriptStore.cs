using System.Collections.Concurrent;
using System.Text.Json;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// Chrome 会议监听实录：按会话 id 追加转写段落（JSONL），供超长会议落盘与 <see cref="Plugins.MeetingTranscriptPlugin"/> 分块读取。
/// </summary>
public interface IMeetingTranscriptStore
{
    Task AppendSegmentAsync(string sessionId, int sequence, string text, CancellationToken ct = default);
    Task<MeetingTranscriptReadResult> ReadChunkAsync(string sessionId, int offsetChars, int maxChars, CancellationToken ct = default);
    Task<MeetingTranscriptMeta> GetMetaAsync(string sessionId, CancellationToken ct = default);
    /// <summary>返回 sequence 严格大于 <paramref name="afterSequenceExclusive"/> 的段落（已按 sequence 升序、同序号取最后一次写入）。</summary>
    Task<MeetingTranscriptSegmentsResult> ListSegmentsAfterAsync(string sessionId, int afterSequenceExclusive, CancellationToken ct = default);
}

public sealed class MeetingTranscriptMeta
{
    public int TotalChars { get; init; }
    public int SegmentCount { get; init; }
}

public sealed class MeetingTranscriptReadResult
{
    public string Text { get; init; } = "";
    public int TotalChars { get; init; }
    public int NextOffset { get; init; }
    public bool HasMore { get; init; }
}

public sealed class MeetingTranscriptSegmentDto
{
    public int Sequence { get; init; }
    public string Text { get; init; } = "";
}

public sealed class MeetingTranscriptSegmentsResult
{
    public IReadOnlyList<MeetingTranscriptSegmentDto> Segments { get; init; } = Array.Empty<MeetingTranscriptSegmentDto>();
    /// <summary>文件中出现的最大 sequence；无数据时为 -1。</summary>
    public int MaxSequenceInFile { get; init; } = -1;
}

public sealed class MeetingTranscriptStore : IMeetingTranscriptStore
{
    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private static string SanitizeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return "";
        var s = sessionId.Trim();
        if (s.Length > 80) s = s[..80];
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') continue;
            return "";
        }
        return s.Length > 0 ? s : "";
    }

    private static string GetRootDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "OfficeCopilot", "MeetingTranscripts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetPath(string safeId) => Path.Combine(GetRootDirectory(), safeId + ".jsonl");

    private SemaphoreSlim LockFor(string safeId) =>
        _locks.GetOrAdd(safeId, static _ => new SemaphoreSlim(1, 1));

    public async Task AppendSegmentAsync(string sessionId, int sequence, string text, CancellationToken ct = default)
    {
        var safe = SanitizeSessionId(sessionId);
        if (string.IsNullOrEmpty(safe))
            throw new ArgumentException("Invalid sessionId.", nameof(sessionId));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        text ??= "";
        var line = JsonSerializer.Serialize(new MeetingTranscriptLine(sequence, text, DateTimeOffset.UtcNow.ToString("O")), JsonLine);
        var path = GetPath(safe);
        var sem = LockFor(safe);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line + "\n", ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private static async Task<List<MeetingTranscriptLine>> LoadAllLinesAsync(string path, CancellationToken ct)
    {
        var list = new List<MeetingTranscriptLine>();
        if (!File.Exists(path))
            return list;
        await using var fs = File.OpenRead(path);
        using var reader = new StreamReader(fs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var l = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (l is null) break;
            if (string.IsNullOrWhiteSpace(l)) continue;
            try
            {
                var row = JsonSerializer.Deserialize<MeetingTranscriptLine>(l, JsonLine);
                if (row != null)
                    list.Add(row);
            }
            catch
            {
                // skip corrupt line
            }
        }
        return list;
    }

    private static string BuildFullText(IReadOnlyList<MeetingTranscriptLine> rows)
    {
        if (rows.Count == 0) return "";
        var ordered = rows
            .GroupBy(r => r.Sequence)
            .Select(g => g.OrderByDescending(x => x.Text?.Length ?? 0).First())
            .OrderBy(g => g.Sequence)
            .ToList();
        return string.Join("\n", ordered.Select(r => r.Text ?? "").Where(t => t.Length > 0));
    }

    public async Task<MeetingTranscriptMeta> GetMetaAsync(string sessionId, CancellationToken ct = default)
    {
        var safe = SanitizeSessionId(sessionId);
        if (string.IsNullOrEmpty(safe))
            return new MeetingTranscriptMeta { TotalChars = 0, SegmentCount = 0 };
        var path = GetPath(safe);
        var sem = LockFor(safe);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await LoadAllLinesAsync(path, ct).ConfigureAwait(false);
            var full = BuildFullText(rows);
            return new MeetingTranscriptMeta { TotalChars = full.Length, SegmentCount = rows.Count };
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<MeetingTranscriptSegmentsResult> ListSegmentsAfterAsync(string sessionId, int afterSequenceExclusive, CancellationToken ct = default)
    {
        var safe = SanitizeSessionId(sessionId);
        if (string.IsNullOrEmpty(safe))
            return new MeetingTranscriptSegmentsResult { Segments = Array.Empty<MeetingTranscriptSegmentDto>(), MaxSequenceInFile = -1 };
        var path = GetPath(safe);
        var sem = LockFor(safe);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await LoadAllLinesAsync(path, ct).ConfigureAwait(false);
            var ordered = rows
                .GroupBy(r => r.Sequence)
                .Select(g => g.OrderByDescending(x => x.Text?.Length ?? 0).First())
                .OrderBy(x => x.Sequence)
                .ToList();
            var maxSeq = ordered.Count > 0 ? ordered[^1].Sequence : -1;
            var slice = ordered
                .Where(x => x.Sequence > afterSequenceExclusive && !string.IsNullOrEmpty(x.Text))
                .Select(x => new MeetingTranscriptSegmentDto { Sequence = x.Sequence, Text = x.Text ?? "" })
                .ToList();
            return new MeetingTranscriptSegmentsResult { Segments = slice, MaxSequenceInFile = maxSeq };
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<MeetingTranscriptReadResult> ReadChunkAsync(string sessionId, int offsetChars, int maxChars, CancellationToken ct = default)
    {
        var safe = SanitizeSessionId(sessionId);
        if (string.IsNullOrEmpty(safe))
        {
            return new MeetingTranscriptReadResult
            {
                Text = "",
                TotalChars = 0,
                NextOffset = 0,
                HasMore = false
            };
        }
        if (offsetChars < 0) offsetChars = 0;
        if (maxChars <= 0) maxChars = 16_000;
        maxChars = Math.Min(maxChars, 256_000);
        var path = GetPath(safe);
        var sem = LockFor(safe);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await LoadAllLinesAsync(path, ct).ConfigureAwait(false);
            var full = BuildFullText(rows);
            var total = full.Length;
            if (offsetChars >= total)
            {
                return new MeetingTranscriptReadResult
                {
                    Text = "",
                    TotalChars = total,
                    NextOffset = total,
                    HasMore = false
                };
            }
            var len = Math.Min(maxChars, total - offsetChars);
            var slice = full.Substring(offsetChars, len);
            var next = offsetChars + len;
            return new MeetingTranscriptReadResult
            {
                Text = slice,
                TotalChars = total,
                NextOffset = next,
                HasMore = next < total
            };
        }
        finally
        {
            sem.Release();
        }
    }

    private sealed record MeetingTranscriptLine(int Sequence, string Text, string Ts);
}
