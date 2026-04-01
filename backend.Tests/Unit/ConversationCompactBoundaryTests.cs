using Microsoft.SemanticKernel.ChatCompletion;
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
        Assert.Contains("hello", body, StringComparison.Ordinal);
        Assert.Contains("[compact_boundary:2026-04-01T12:00:00.000Z]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFirstRemovableChatIndex_NoBoundary_ReturnsOne()
    {
        var h = new ChatHistory("sys");
        h.AddUserMessage("u1");
        h.AddAssistantMessage("a1");
        Assert.Equal(1, ConversationCompactBoundary.GetFirstRemovableChatIndex(h));
    }

    [Fact]
    public void GetFirstRemovableChatIndex_WithBoundaryAndAnchor_StartsAfterAnchor()
    {
        var h = new ChatHistory("sys");
        var summary = ConversationCompactBoundary.BuildSummaryMessageBody("s", DateTimeOffset.UtcNow);
        h.AddUserMessage(summary);
        h.AddAssistantMessage("anchor");
        h.AddUserMessage("old");
        Assert.Equal(3, ConversationCompactBoundary.GetFirstRemovableChatIndex(h));
    }

    [Fact]
    public void GetFirstRemovableChatIndex_OnlySystemAndSummary_ReturnsCount()
    {
        var h = new ChatHistory("sys");
        h.AddUserMessage(ConversationCompactBoundary.BuildSummaryMessageBody("x", DateTimeOffset.UtcNow));
        Assert.Equal(h.Count, ConversationCompactBoundary.GetFirstRemovableChatIndex(h));
    }
}
