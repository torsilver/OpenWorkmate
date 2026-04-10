using System.Text.Json;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services.Chat;
using Xunit;

namespace backend.Tests.Unit;

public sealed class TimelineBlockStreamCoordinatorTests
{
    private const string Sid = "sess1";

    [Fact]
    public void BeginRound_then_think_answer_think_increments_blockSeq()
    {
        var c = new TimelineBlockStreamCoordinator();
        c.BeginRound(Sid);

        var (a0, k0) = c.EnsureChunkBlock(Sid, TimelineBlockStreamCoordinator.KindThink);
        Assert.Equal(0, a0);
        Assert.Equal("think", k0);

        var (a1, k1) = c.EnsureChunkBlock(Sid, TimelineBlockStreamCoordinator.KindAnswer);
        Assert.Equal(1, a1);
        Assert.Equal("answer", k1);

        var (a2, k2) = c.EnsureChunkBlock(Sid, TimelineBlockStreamCoordinator.KindThink);
        Assert.Equal(2, a2);
        Assert.Equal("think", k2);

        c.EndRound(Sid);
    }

    [Fact]
    public void Same_kind_reuses_blockSeq()
    {
        var c = new TimelineBlockStreamCoordinator();
        c.BeginRound(Sid);
        var (s0, _) = c.EnsureChunkBlock(Sid, TimelineBlockStreamCoordinator.KindThink);
        var (s0b, _) = c.EnsureChunkBlock(Sid, TimelineBlockStreamCoordinator.KindThink);
        Assert.Equal(s0, s0b);
        c.EndRound(Sid);
    }

    [Fact]
    public void OnToolInvocationStart_clears_active_so_next_chunk_new_segment()
    {
        var c = new TimelineBlockStreamCoordinator();
        c.BeginRound(Sid);
        c.EnsureChunkBlock(Sid, TimelineBlockStreamCoordinator.KindAnswer);
        c.OnToolInvocationStart(Sid);
        var (after, k) = c.EnsureChunkBlock(Sid, TimelineBlockStreamCoordinator.KindAnswer);
        Assert.Equal(1, after);
        Assert.Equal("answer", k);
        c.EndRound(Sid);
    }

    [Fact]
    public void WsMessage_json_includes_blockSeq_blockKind_camelCase()
    {
        var msg = new OfficeCopilot.Server.WsMessage
        {
            Type = "stream_chunk",
            Content = "x",
            BlockSeq = 1,
            BlockKind = "answer"
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        Assert.Contains("\"blockSeq\":1", json);
        Assert.Contains("\"blockKind\":\"answer\"", json);
    }
}
