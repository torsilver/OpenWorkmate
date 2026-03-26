using OfficeCopilot.Server.Services.SkillVm;
using Xunit;

namespace backend.Tests.Unit;

public class SkillVmGotoPolicyTests
{
    [Fact]
    public void SameSkill_AllowsExistingSegment()
    {
        var m = new SkillVmManifest
        {
            Segments =
            [
                new SkillVmSegmentDef { Id = "a", Order = 1 },
                new SkillVmSegmentDef { Id = "b", Order = 2 }
            ]
        };
        Assert.True(SkillVmGotoPolicy.IsGotoAllowed(m, m, "s", "s", "b"));
    }

    [Fact]
    public void CrossSkill_RequiresWhitelistWhenSet()
    {
        var from = new SkillVmManifest
        {
            AllowedGotoTargets = ["other:x"]
        };
        var to = new SkillVmManifest
        {
            Segments = [new SkillVmSegmentDef { Id = "x", Order = 1 }]
        };
        Assert.False(SkillVmGotoPolicy.IsGotoAllowed(from, to, "from", "other", "y"));
        Assert.True(SkillVmGotoPolicy.IsGotoAllowed(from, to, "from", "other", "x"));
    }
}
