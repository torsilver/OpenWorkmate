using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class ConversationCompactBoundaryTests
{
    [Fact]
    public void BuildSummaryMessageBody_IncludesPrefixBoundaryAndIso()
    {
        var utc = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var body = ConversationCompactBoundary.BuildSummaryMessageBody("hello", utc);
        Assert.Contains(ConversationCompactBoundary.SummaryPrefix, body, StringComparison.Ordinal);
        Assert.Contains(ConversationCompactBoundary.SummaryScopeNotice, body, StringComparison.Ordinal);
        Assert.Contains(ConversationCompactBoundary.SummaryXmlOpen, body, StringComparison.Ordinal);
        Assert.Contains(ConversationCompactBoundary.SummaryXmlClose, body, StringComparison.Ordinal);
        Assert.Contains("hello", body, StringComparison.Ordinal);
        Assert.Contains("[compact_boundary:2026-04-01T12:00:00.000Z]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFirstRemovableChatIndex_NoBoundary_ReturnsOne()
    {
        var h = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "u1"),
            new(ChatRole.Assistant, "a1")
        };
        Assert.Equal(1, ConversationCompactBoundary.GetFirstRemovableChatIndex(h));
    }

    [Fact]
    public void GetFirstRemovableChatIndex_WithBoundaryAndAnchor_StartsAfterAnchor()
    {
        var summary = ConversationCompactBoundary.BuildSummaryMessageBody("s", DateTimeOffset.UtcNow);
        var h = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, summary),
            new(ChatRole.Assistant, "anchor"),
            new(ChatRole.User, "old")
        };
        Assert.Equal(3, ConversationCompactBoundary.GetFirstRemovableChatIndex(h));
    }

    [Fact]
    public void GetFirstRemovableChatIndex_OnlySystemAndSummary_ReturnsCount()
    {
        var h = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, ConversationCompactBoundary.BuildSummaryMessageBody("x", DateTimeOffset.UtcNow))
        };
        Assert.Equal(h.Count, ConversationCompactBoundary.GetFirstRemovableChatIndex(h));
    }
}
