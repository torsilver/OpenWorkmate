using OpenWorkmate.Server.Services.Telemetry;
using Xunit;

namespace backend.Tests.Unit;

public sealed class TelemetryIngestDetailGateTests
{
    [Theory]
    [InlineData("debug", "p2", true)]
    [InlineData("information", "p2", false)]
    [InlineData("error", "p0", true)]
    [InlineData("error", "p1", false)]
    [InlineData("off", "p0", false)]
    public void AllowsEnqueue_respects_cap(string ingest, string detail, bool expected) =>
        Assert.Equal(expected, TelemetryIngestDetailGate.AllowsEnqueue(ingest, detail));

    [Theory]
    [InlineData("debug", "debug", "p2", true)]
    [InlineData("debug", "information", "p2", false)]
    [InlineData("information", "error", "p1", false)]
    [InlineData("information", "information", "p1", true)]
    [InlineData("information", null, "p1", true)]
    [InlineData("off", "information", "p0", false)]
    [InlineData("information", "off", "p0", false)]
    public void AllowsEnqueueCombined_takes_stricter_of_session_and_relay(
        string sessionIngest,
        string? relayCap,
        string detail,
        bool expected) =>
        Assert.Equal(expected, TelemetryIngestDetailGate.AllowsEnqueueCombined(sessionIngest, relayCap, detail));
}
