using System.Text.Json;
using OfficeCopilot.Server.Services.ToolInvocation;
using Xunit;

namespace backend.Tests.Unit;

public class ToolResultNormalizeTests
{
    [Fact]
    public void NormalizeToolResultToString_Null_ReturnsNull()
    {
        Assert.Null(ToolStatusNotifier.NormalizeToolResultToString(null));
    }

    [Fact]
    public void NormalizeToolResultToString_String_Passthrough()
    {
        const string s = "[计划已生成] planId=abc123，标题：测试。";
        Assert.Equal(s, ToolStatusNotifier.NormalizeToolResultToString(s));
    }

    [Fact]
    public void NormalizeToolResultToString_JsonElementString_Unwraps()
    {
        var je = JsonDocument.Parse("\"hello\"").RootElement;
        Assert.Equal("hello", ToolStatusNotifier.NormalizeToolResultToString(je));
    }

    [Fact]
    public void NormalizeToolResultToString_JsonElementObject_ToString()
    {
        var je = JsonDocument.Parse("{\"a\":1}").RootElement;
        var t = ToolStatusNotifier.NormalizeToolResultToString(je);
        Assert.NotNull(t);
        Assert.Contains("a", t, StringComparison.Ordinal);
    }
}
