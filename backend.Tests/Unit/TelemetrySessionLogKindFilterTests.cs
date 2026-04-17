using Microsoft.AspNetCore.Http;
using OfficeCopilot.Server.Services.Telemetry;
using Xunit;

namespace backend.Tests.Unit;

public sealed class TelemetrySessionLogKindFilterTests
{
    [Fact]
    public void ParseFromQuery_splits_comma_and_repeated_keys()
    {
        var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["telemetryLogKinds"] = new[] { "a,b", "c" }
        });
        var set = TelemetrySessionLogKindFilter.ParseFromQuery(q);
        Assert.NotNull(set);
        Assert.Equal(3, set!.Count);
        Assert.Contains("a", set);
        Assert.Contains("b", set);
        Assert.Contains("c", set);
    }

    [Fact]
    public void ParseFromQuery_missing_returns_null()
    {
        var q = new QueryCollection();
        Assert.Null(TelemetrySessionLogKindFilter.ParseFromQuery(q));
    }
}
