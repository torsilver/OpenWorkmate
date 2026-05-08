using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class MeetingTranscriptStoreTests
{
    [Fact]
    public async Task Append_And_ReadChunk_JoinsBySequence_Ordered()
    {
        var sid = "unittest_" + Guid.NewGuid().ToString("N");
        var store = new MeetingTranscriptStore();
        await store.AppendSegmentAsync(sid, 1, "second", CancellationToken.None);
        await store.AppendSegmentAsync(sid, 0, "first", CancellationToken.None);
        var r = await store.ReadChunkAsync(sid, 0, 10_000, CancellationToken.None);
        Assert.Equal("first\nsecond", r.Text);
        Assert.Equal(r.TotalChars, r.Text.Length);
        Assert.False(r.HasMore);

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenWorkmate", "MeetingTranscripts", sid + ".jsonl");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }

    [Fact]
    public async Task Append_WritesChineseLiteralsInJsonl_NotUnicodeEscapes()
    {
        var sid = "unittest_cn_" + Guid.NewGuid().ToString("N");
        var store = new MeetingTranscriptStore();
        await store.AppendSegmentAsync(sid, 0, "中文实录", CancellationToken.None);
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenWorkmate", "MeetingTranscripts", sid + ".jsonl");
        try
        {
            var raw = await File.ReadAllTextAsync(path);
            Assert.Contains("中文实录", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u4e2d", raw, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    [Fact]
    public async Task ListSegmentsAfter_ReturnsOnlyNewSequences()
    {
        var sid = "unittest_seg_" + Guid.NewGuid().ToString("N");
        var store = new MeetingTranscriptStore();
        await store.AppendSegmentAsync(sid, 0, "a", CancellationToken.None);
        await store.AppendSegmentAsync(sid, 1, "b", CancellationToken.None);
        var r0 = await store.ListSegmentsAfterAsync(sid, -1, CancellationToken.None);
        Assert.Equal(2, r0.Segments.Count);
        Assert.Equal(1, r0.MaxSequenceInFile);
        var r1 = await store.ListSegmentsAfterAsync(sid, 0, CancellationToken.None);
        Assert.Single(r1.Segments);
        Assert.Equal(1, r1.Segments[0].Sequence);
        Assert.Equal("b", r1.Segments[0].Text);
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenWorkmate", "MeetingTranscripts", sid + ".jsonl");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }
}
