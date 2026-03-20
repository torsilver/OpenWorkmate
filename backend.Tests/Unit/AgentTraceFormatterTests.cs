using System.Text.Json;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class AgentTraceFormatterTests
{
    [Fact]
    public void TruncateDetail_AppendsEllipsisWhenOverLimit()
    {
        var s = new string('a', AgentTraceFormatter.DefaultMaxDetailChars + 50);
        var t = AgentTraceFormatter.TruncateDetail(s, maxChars: 100);
        Assert.Contains("…(已截断)", t, StringComparison.Ordinal);
        Assert.True(t.Length < s.Length);
    }

    [Fact]
    public void BuildMemoryTrace_ZeroHits_HasExplanatoryDetail()
    {
        var (title, detail) = AgentTraceFormatter.BuildMemoryTrace(
            Array.Empty<(string, string, double)>(),
            Array.Empty<(string, string, double)>(),
            5, 3);
        Assert.Contains("0 条", title, StringComparison.Ordinal);
        Assert.Contains("无命中", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTwoStageToolTrace_OkTwoStage_IncludesSubcategoriesAndSampleFunctions()
    {
        var outcome = new ToolSelectionOutcome(
            SelectedPairs: new[] { ("Word", "word_body_read"), ("CLI", "run_command") },
            ReasonCode: "ok_two_stage",
            SelectedSubcategoryIds: new[] { "Word-获取信息" },
            CandidateFunctionCount: 10,
            MergedFunctionCount: 2);
        var (title, detail) = AgentTraceFormatter.BuildTwoStageToolTrace(outcome, maxFunctionLines: 10);
        Assert.Contains("两阶段", title, StringComparison.Ordinal);
        Assert.Contains("Word-获取信息", detail, StringComparison.Ordinal);
        Assert.Contains("Word.word_body_read", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildContextSummarizationSuccessTrace_MentionsMessagesAndSummaryLimit()
    {
        var ctx = new ContextWindowConfig { SummarizationTriggerRatio = 0.75, SummarizationMaxSummaryChars = 2000 };
        var (title, detail) = AgentTraceFormatter.BuildContextSummarizationSuccessTrace(8, 120, ctx, historyOffloadAttempted: true);
        Assert.Contains("压缩", title, StringComparison.Ordinal);
        Assert.Contains("8", detail, StringComparison.Ordinal);
        Assert.Contains("2000", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildContextTruncateTrace_MentionsThresholdAndKeep()
    {
        var (title, detail) = AgentTraceFormatter.BuildContextTruncateTrace(3, 4, 500, 0.85, 9000, 8000);
        Assert.Contains("3", title, StringComparison.Ordinal);
        Assert.Contains("500", detail, StringComparison.Ordinal);
        Assert.Contains("4", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildContextSummarizationFailureTrace_IncludesReason()
    {
        var (title, detail) = AgentTraceFormatter.BuildContextSummarizationFailureTrace("timeout");
        Assert.Contains("未生效", title, StringComparison.Ordinal);
        Assert.Contains("timeout", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WsMessage_AgentTrace_SerializesCamelCase()
    {
        var msg = new WsMessage
        {
            Type = "agent_trace",
            Content = "t",
            TraceCategory = "toolSelection",
            TraceTitle = "title-x",
            TraceDetail = "detail-y"
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        Assert.Contains("\"traceCategory\":\"toolSelection\"", json, StringComparison.Ordinal);
        Assert.Contains("\"traceTitle\":\"title-x\"", json, StringComparison.Ordinal);
        Assert.Contains("\"traceDetail\":\"detail-y\"", json, StringComparison.Ordinal);
    }
}
