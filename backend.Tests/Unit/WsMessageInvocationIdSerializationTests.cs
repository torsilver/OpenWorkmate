using System.Text.Json;
using OpenWorkmate.Server;
using Xunit;

namespace backend.Tests.Unit;

public sealed class WsMessageInvocationIdSerializationTests
{
    [Fact]
    public void Serialize_WsMessage_IncludesInvocationId_CamelCase()
    {
        var msg = new WsMessage
        {
            Type = "tool_invocation_start",
            InvocationId = "deadbeef",
            Plugin = "Word",
            Function = "read",
            IsSubtask = true
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        Assert.Contains("\"invocationId\":\"deadbeef\"", json);
        Assert.Contains("\"isSubtask\":true", json);
    }
}
