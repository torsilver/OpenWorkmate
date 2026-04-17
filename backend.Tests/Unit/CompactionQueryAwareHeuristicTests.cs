using System.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class CompactionQueryAwareHeuristicTests
{
    [Fact]
    public void BuildTerms_SkipsShortTokensAndDedupes()
    {
        var terms = CompactionQueryAwareHeuristic.BuildTerms("x aa bb BB 中文测试");
        Assert.Contains("中文测试", terms);
        Assert.DoesNotContain("x", terms);
        Assert.Single(terms, t => t.Equals("bb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OverlapScore_CountsDistinctTermHits()
    {
        var terms = new[] { "foo", "bar" };
        Assert.Equal(2, CompactionQueryAwareHeuristic.OverlapScore("Foo and BAR here", terms));
        Assert.Equal(0, CompactionQueryAwareHeuristic.OverlapScore("none", terms));
    }

    [Fact]
    public void TryTrim_RemovesOldestNonOverlappingWhenPressureHigh()
    {
        var ctx = new ContextWindowConfig
        {
            CompactionQueryAwareHeuristicEnabled = true,
            CompactionQueryAwareMaxRemovalsPerTurn = 5,
            CompactionQueryAwareTokenPressureRatio = 0.01,
            MaxContextTokens = 2000,
            ReservedOutputTokens = 0,
            CharsPerToken = 2
        };
        var session = new SessionConfig { MinTurnsToKeep = 1 };
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, new string('s', 400)),
            new(ChatRole.User, "irrelevant old blob one"),
            new(ChatRole.Assistant, new string('a', 400)),
            new(ChatRole.User, "irrelevant old blob two"),
            new(ChatRole.Assistant, new string('b', 400)),
        };

        var removed = CompactionQueryAwareHeuristic.TryTrimLowRelevanceOldestRemovable(
            history,
            "freshkeyword unique",
            ctx,
            session,
            effectiveMaxContextTokens: 2000,
            NullLogger.Instance,
            "sid",
            "rid");

        Assert.True(removed > 0);
        Assert.DoesNotContain("irrelevant old blob one", string.Join("", history.Select(m => m.Text ?? "")), StringComparison.Ordinal);
    }

    [Fact]
    public void TryTrim_DoesNothingWhenDisabled()
    {
        var ctx = new ContextWindowConfig
        {
            CompactionQueryAwareHeuristicEnabled = false,
            CompactionQueryAwareTokenPressureRatio = 0.01,
            MaxContextTokens = 2000,
            ReservedOutputTokens = 0
        };
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "s"),
            new(ChatRole.User, "old"),
            new(ChatRole.Assistant, "a"),
        };
        var before = history.Count;
        var removed = CompactionQueryAwareHeuristic.TryTrimLowRelevanceOldestRemovable(
            history,
            "nomatch",
            ctx,
            new SessionConfig { MinTurnsToKeep = 1 },
            2000,
            NullLogger.Instance,
            "sid",
            "rid");
        Assert.Equal(0, removed);
        Assert.Equal(before, history.Count);
    }
}
