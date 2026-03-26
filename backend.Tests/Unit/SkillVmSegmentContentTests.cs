using OfficeCopilot.Server.Services.SkillVm;
using Xunit;

namespace backend.Tests.Unit;

public class SkillVmSegmentContentTests
{
    [Fact]
    public void GetFirstSegmentId_orders_by_order_then_id()
    {
        var m = new SkillVmManifest
        {
            SkillId = "t",
            Segments =
            {
                new SkillVmSegmentDef { Id = "b", Order = 2 },
                new SkillVmSegmentDef { Id = "a", Order = 1 }
            }
        };
        Assert.Equal("a", SkillVmSegmentContent.GetFirstSegmentId(m));
    }

    [Fact]
    public void GetNextSegmentId_returns_next_in_order()
    {
        var m = new SkillVmManifest
        {
            Segments =
            {
                new SkillVmSegmentDef { Id = "a", Order = 1 },
                new SkillVmSegmentDef { Id = "b", Order = 2 }
            }
        };
        Assert.Equal("b", SkillVmSegmentContent.GetNextSegmentId(m, "a"));
        Assert.Null(SkillVmSegmentContent.GetNextSegmentId(m, "b"));
    }

    [Fact]
    public void ExtractSegmentFromBody_finds_heading()
    {
        var body = "## a\n\nfirst\n\n## b\n\nsecond\n";
        var seg = SkillVmSegmentContent.ExtractSegmentFromBody(body, "b");
        Assert.NotNull(seg);
        Assert.Contains("second", seg, StringComparison.Ordinal);
    }
}
