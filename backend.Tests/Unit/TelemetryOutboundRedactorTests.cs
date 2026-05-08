using System.Text.Json;
using OpenWorkmate.Server.Services.Telemetry;
using Xunit;

namespace backend.Tests.Unit;

public sealed class TelemetryOutboundRedactorTests
{
    private static readonly TelemetryTransmissionPolicyFile Policy = TelemetryTransmissionPolicyDefaults.CreateDefault();

    [Fact]
    public void Minimal_assistant_turn_final_truncates_message()
    {
        var msg = new string('a', 500);
        var (m, p) = TelemetryOutboundRedactor.Apply("minimal", "assistant_turn_final", msg, null, Policy);
        Assert.NotNull(m);
        Assert.Equal(Policy.Minimal.AssistantTurnFinalMsgPreviewMax + 1, m!.Length); // + ellipsis
        Assert.EndsWith("…", m, StringComparison.Ordinal);
    }

    [Fact]
    public void Minimal_tool_invocation_end_uses_tool_cap()
    {
        var msg = new string('b', 200);
        var (m, _) = TelemetryOutboundRedactor.Apply("minimal", "tool_invocation_end", msg, null, Policy);
        Assert.Equal(Policy.Minimal.ToolInvocationEndMsgPreviewMax + 1, m!.Length);
    }

    [Fact]
    public void Traces_truncates_payload_serialized_length()
    {
        var payload = JsonSerializer.SerializeToElement(new { x = new string('c', 5000) });
        var (_, p) = TelemetryOutboundRedactor.Apply("traces", "x", "hi", payload, Policy);
        Assert.NotNull(p);
        var s = JsonSerializer.Serialize(p, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True(s.Length <= Policy.Traces.PayloadMax + 32);
    }

    [Fact]
    public void Full_respects_full_caps()
    {
        var msg = new string('d', 100_000);
        var (m, _) = TelemetryOutboundRedactor.Apply("full", "e", msg, null, Policy);
        Assert.True(m!.Length <= Policy.Full.MsgMax + 1);
    }

    [Fact]
    public void Merge_remote_partial_fills_defaults()
    {
        var merged = TelemetryTransmissionPolicyDefaults.Merge(new TelemetryTransmissionPolicyFile
        {
            SchemaVersion = 2,
            Minimal = new MinimalTransmissionLimits { AssistantTurnFinalMsgPreviewMax = 100 }
        });
        Assert.Equal(2, merged.SchemaVersion);
        Assert.Equal(100, merged.Minimal.AssistantTurnFinalMsgPreviewMax);
        Assert.Equal(120, merged.Minimal.ToolInvocationEndMsgPreviewMax);
    }
}
