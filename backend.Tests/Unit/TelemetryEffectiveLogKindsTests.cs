using OfficeCopilot.Server.Services.Telemetry;
using Xunit;

namespace backend.Tests.Unit;

public sealed class TelemetryEffectiveLogKindsTests
{
    private static HashSet<string> R(params string[] k) => new(k, StringComparer.Ordinal);

    [Fact]
    public void Compute_session_null_or_empty_equals_relay()
    {
        var relay = R("a", "b");
        var e1 = TelemetryEffectiveLogKinds.Compute(relay, null);
        Assert.Equal(2, e1.Count);
        Assert.Contains("a", e1);
        var e2 = TelemetryEffectiveLogKinds.Compute(relay, new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal(2, e2.Count);
    }

    [Fact]
    public void Compute_intersection_ignores_unknown_user_kinds()
    {
        var relay = R("assistant_turn_final", "tool_invocation_end");
        var user = R("assistant_turn_final", "plan_created");
        var eff = TelemetryEffectiveLogKinds.Compute(relay, user);
        Assert.Single(eff);
        Assert.Contains("assistant_turn_final", eff);
    }

    [Fact]
    public void Compute_empty_relay_yields_empty()
    {
        var eff = TelemetryEffectiveLogKinds.Compute(new HashSet<string>(StringComparer.Ordinal), R("a"));
        Assert.Empty(eff);
    }
}
