using System.Linq;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Unit;

public class BuiltInAgentProfileDefaultsTests
{
    [Fact]
    public void CreateList_HasExpectedIdsAndUnique()
    {
        var list = BuiltInAgentProfileDefaults.CreateList();
        Assert.True(list.Count >= 7);
        var ids = list.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("default", ids, StringComparer.Ordinal);
        Assert.Equal("default", list[0].Id);
    }

    [Fact]
    public void MergeWithUserProfiles_Empty_ReturnsBuiltIns()
    {
        var merged = BuiltInAgentProfileDefaults.MergeWithUserProfiles(Array.Empty<AgentProfileEntry>());
        Assert.Equal(BuiltInAgentProfileDefaults.CreateList().Count, merged.Count);
        Assert.Equal("moe", merged[1].Id);
    }

    [Fact]
    public void MergeWithUserProfiles_OnlyDefault_AppendsMissingBuiltIns()
    {
        var user = new List<AgentProfileEntry>
        {
            new() { Id = "default", DisplayName = "默认助手", SystemPromptSuffix = null }
        };
        var merged = BuiltInAgentProfileDefaults.MergeWithUserProfiles(user);
        Assert.Equal(7, merged.Count);
        Assert.Equal("default", merged[0].Id);
        Assert.Equal("moe", merged[1].Id);
        Assert.Equal("zen", merged[6].Id);
    }

    [Fact]
    public void MergeWithUserProfiles_UserOverridesBuiltinSlot()
    {
        var user = new List<AgentProfileEntry>
        {
            new() { Id = "moe", DisplayName = "我的萌", SystemPromptSuffix = "自定义后缀" }
        };
        var merged = BuiltInAgentProfileDefaults.MergeWithUserProfiles(user);
        var moe = merged.First(p => p.Id == "moe");
        Assert.Equal("我的萌", moe.DisplayName);
        Assert.Equal("自定义后缀", moe.SystemPromptSuffix);
    }

    [Fact]
    public void MergeWithUserProfiles_CustomIdAppendedAfterBuiltins()
    {
        var user = new List<AgentProfileEntry>
        {
            new() { Id = "default", DisplayName = "默认助手" },
            new() { Id = "lab", DisplayName = "实验室" }
        };
        var merged = BuiltInAgentProfileDefaults.MergeWithUserProfiles(user);
        Assert.Equal("lab", merged[^1].Id);
    }
}
