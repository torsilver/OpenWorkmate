using OpenWorkmate.Server.Services.DynamicTooling;
using Xunit;

namespace OpenWorkmate.Server.Tests.Unit;

public sealed class ToolCatalogSuccessBoostTests
{
    [Fact]
    public void RecordSuccess_IncrementsPerFunction_Capped()
    {
        var fn = "func_boost_" + Guid.NewGuid().ToString("N");
        ToolCatalogSuccessBoost.RecordSuccess(fn);
        ToolCatalogSuccessBoost.RecordSuccess(fn);
        var snap = ToolCatalogSuccessBoost.GetSnapshot();
        Assert.True(snap.TryGetValue(fn, out var n));
        Assert.Equal(2, n);
    }
}
