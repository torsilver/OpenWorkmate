using System.Collections;
using Microsoft.SemanticKernel;
using OfficeCopilot.Server.Services.SemanticKernel;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class StreamingToolCallDeltaHelperTests
{
    [Fact]
    public void ExtractFromItems_Null_YieldsNothing()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        Assert.Empty(StreamingToolCallDeltaHelper.ExtractFromItems(null, budget).ToList());
    }

    [Fact]
    public void ExtractFromItems_Empty_YieldsNothing()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        Assert.Empty(StreamingToolCallDeltaHelper.ExtractFromItems(Array.Empty<object>(), budget).ToList());
    }

    [Fact]
    public void ExtractFromItems_IgnoresNonFunctionUpdateContent()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new ArrayList { new StreamingTextContent("hello") };
        Assert.Empty(StreamingToolCallDeltaHelper.ExtractFromItems(items, budget).ToList());
    }

    [Fact]
    public void ExtractFromItems_NameAndArguments_YieldsDelta()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new ArrayList
        {
            new StreamingFunctionCallUpdateContent("call-1", "Plugin-func", "{\"a\":1}", 0)
        };
        var list = StreamingToolCallDeltaHelper.ExtractFromItems(items, budget).ToList();
        Assert.Single(list);
        Assert.Equal("call-1", list[0].CallId);
        Assert.Equal("Plugin-func", list[0].ToolName);
        Assert.Equal("{\"a\":1}", list[0].ArgumentsDelta);
        Assert.Equal(7, budget["call-1"]);
    }

    [Fact]
    public void ExtractFromItems_NoCallId_UsesIndexKeyAndCallIdOut()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new ArrayList
        {
            new StreamingFunctionCallUpdateContent(null!, "n", "x", 2)
        };
        var list = StreamingToolCallDeltaHelper.ExtractFromItems(items, budget).ToList();
        Assert.Single(list);
        Assert.Equal("i2", list[0].CallId);
        Assert.Equal(1, budget["i2"]);
    }

    [Fact]
    public void ExtractFromItems_RespectsCumulativeCap_TruncatesSecondChunk()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        var cap = StreamingToolCallDeltaHelper.MaxArgumentsCumulativeCharsPerCall;
        var first = new string('a', cap - 3);
        var items = new ArrayList
        {
            new StreamingFunctionCallUpdateContent("c", "f", first, 0),
            new StreamingFunctionCallUpdateContent("c", "f", "bbbbbb", 0)
        };
        var list = StreamingToolCallDeltaHelper.ExtractFromItems(items, budget).ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(cap - 3, list[0].ArgumentsDelta.Length);
        Assert.Equal("bbb", list[1].ArgumentsDelta);
        Assert.Equal(cap, budget["c"]);
    }

    [Fact]
    public void ExtractFromItems_AfterCap_DropsFurtherArgumentDeltas()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        var cap = StreamingToolCallDeltaHelper.MaxArgumentsCumulativeCharsPerCall;
        var items = new ArrayList
        {
            new StreamingFunctionCallUpdateContent("c", "f", new string('z', cap), 0),
            new StreamingFunctionCallUpdateContent("c", "f", "more", 0)
        };
        var list = StreamingToolCallDeltaHelper.ExtractFromItems(items, budget).ToList();
        Assert.Single(list);
        Assert.Equal(cap, list[0].ArgumentsDelta.Length);
    }
}
