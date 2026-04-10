using System.ComponentModel;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;

namespace OfficeCopilot.Server.Plugins;

/// <summary>
/// 会议实录读取：与 Chrome 侧栏 POST 落盘的会话文本配合，支持分块读取以生成超长会议纪要。
/// </summary>
[CopilotPluginId("MeetingTranscript")]
public sealed class MeetingTranscriptPlugin
{
    private readonly IMeetingTranscriptStore _store;

    public MeetingTranscriptPlugin(IMeetingTranscriptStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    [ToolFunction("meeting_transcript_read")]
    [Description("Read a chunk of meeting transcript text persisted for a session (from Chrome meeting listener). Use sessionId from the user's message. Call repeatedly with nextOffset until hasMore is false to read the entire transcript before summarizing.")]
    public async Task<string> MeetingTranscriptReadAsync(
        [Description("Session id (e.g. meeting_abc123)")] string sessionId,
        [Description("Character offset; use 0 first, then the nextOffset from the previous call")] int offsetChars = 0,
        [Description("Max characters to return (default 16000)")] int maxChars = 16_000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "[Error] sessionId is required.";
        try
        {
            var r = await _store.ReadChunkAsync(sessionId.Trim(), offsetChars, maxChars, cancellationToken).ConfigureAwait(false);
            if (r.TotalChars == 0 && string.IsNullOrEmpty(r.Text))
                return "[Error] No transcript found for this sessionId. Confirm the meeting was recorded and segments were saved.";
            return
                $"totalChars={r.TotalChars}\nnextOffset={r.NextOffset}\nhasMore={r.HasMore}\n\n---\n\n{r.Text}";
        }
        catch (Exception ex)
        {
            return "[Error] " + ex.Message;
        }
    }

    [ToolFunction("meeting_transcript_meta")]
    [Description("Get total character count and segment line count for a meeting transcript session without loading full text.")]
    public async Task<string> MeetingTranscriptMetaAsync(
        [Description("Session id")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "[Error] sessionId is required.";
        try
        {
            var m = await _store.GetMetaAsync(sessionId.Trim(), cancellationToken).ConfigureAwait(false);
            return $"totalChars={m.TotalChars}, segmentLines={m.SegmentCount}";
        }
        catch (Exception ex)
        {
            return "[Error] " + ex.Message;
        }
    }
}
