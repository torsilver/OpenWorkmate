using System.Collections.Generic;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class DynamicToolingActivateSkillGateTests
{
    [Fact]
    public void ShouldBlock_False_WhenNoToolSearchYet()
    {
        var state = CreateState(skillCount: 1);
        state.SearchInvocationCount = 0;
        Assert.False(DynamicToolingActivateSkillGate.ShouldBlock(state, out var msg));
        Assert.Equal("", msg);
    }

    [Fact]
    public void ShouldBlock_True_WhenToolSearchedAndSkillsExistButNoSkillSearch()
    {
        var state = CreateState(skillCount: 1);
        state.SearchInvocationCount = 1;
        state.SkillSearchInvocationCount = 0;
        Assert.True(DynamicToolingActivateSkillGate.ShouldBlock(state, out var msg));
        Assert.Contains("search_available_skills", msg, StringComparison.Ordinal);
        Assert.Contains("search_available_tools", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldBlock_False_AfterSkillSearch()
    {
        var state = CreateState(skillCount: 1);
        state.SearchInvocationCount = 1;
        state.SkillSearchInvocationCount = 1;
        Assert.False(DynamicToolingActivateSkillGate.ShouldBlock(state, out _));
    }

    [Fact]
    public void ShouldBlock_False_WhenSkillCatalogEmpty()
    {
        var state = CreateState(skillCount: 0);
        state.SearchInvocationCount = 1;
        Assert.False(DynamicToolingActivateSkillGate.ShouldBlock(state, out _));
    }

    private static DynamicToolingTurnState CreateState(int skillCount)
    {
        var cfg = new DynamicToolingConfig();
        var reg = new ToolRegistry();
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        SkillCatalogIndex skills;
        if (skillCount <= 0)
            skills = SkillCatalogIndex.Empty;
        else
            skills = SkillCatalogIndex.BuildFromEnabledSkills(new List<SkillDefinition>
            {
                new() { Id = "gate_test_skill", Enabled = true, Name = "T", Description = "d" }
            });
        return new DynamicToolingTurnState(cfg, catalog, skills);
    }
}
