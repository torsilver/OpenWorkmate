using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services.ToolInvocation;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ToolInvocationMeaiResultInspectorTests
{
    [Fact]
    public void GetEffectivePayload_non_envelope_returns_same()
    {
        const string s = "plain";
        Assert.Same(s, ToolInvocationMeaiResultInspector.GetEffectivePayload(s));
        Assert.Null(ToolInvocationMeaiResultInspector.GetEffectivePayload(null));
    }

    [Fact]
    public void TryGetEnvelopeFailureMessage_plain_object_returns_false()
    {
        Assert.False(ToolInvocationMeaiResultInspector.TryGetEnvelopeFailureMessage(
            "ok", "P", "f", out var msg));
        Assert.Equal("", msg);
    }

    [Fact]
    public void TryGetEnvelopeFailureMessage_exception_status_produces_tool_invoke_failure_prefix()
    {
        var frType = typeof(FunctionInvokingChatClient).GetNestedType("FunctionInvocationResult", BindingFlags.Public);
        Assert.NotNull(frType);
        var statusType = typeof(FunctionInvokingChatClient).GetNestedType("FunctionInvocationStatus", BindingFlags.Public);
        Assert.NotNull(statusType);
        var exStatus = Enum.Parse(statusType, "Exception");
        var call = new FunctionCallContent("call-1", "demo_tool", new Dictionary<string, object?>());
        var ctor = frType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(bool), statusType, typeof(FunctionCallContent), typeof(object), typeof(Exception)],
            modifiers: null);
        Assert.NotNull(ctor);
        var fir = ctor.Invoke([false, exStatus, call, null, new ArgumentException("missing data")]);
        Assert.True(ToolInvocationMeaiResultInspector.TryGetEnvelopeFailureMessage(
            fir, "Excel", "excel_range_write", out var msg));
        Assert.StartsWith("[工具调用失败] Excel.excel_range_write:", msg, StringComparison.Ordinal);
        Assert.Contains("data", msg, StringComparison.Ordinal);
    }
}
