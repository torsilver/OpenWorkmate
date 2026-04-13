using System.Collections.Generic;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class SkillCatalogIndexTests
{
    [Fact]
    public void BuildFromEnabledSkills_ExcludesDisabled()
    {
        var idx = SkillCatalogIndex.BuildFromEnabledSkills(new List<SkillDefinition>
        {
            new() { Id = "a", Enabled = true, Name = "A", Description = "alpha" },
            new() { Id = "b", Enabled = false, Name = "B", Description = "beta" }
        });
        Assert.Single(idx.Entries);
        Assert.Equal("a", idx.Entries[0].SkillId, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_KeywordMatchesDescription()
    {
        var idx = SkillCatalogIndex.BuildFromEnabledSkills(new List<SkillDefinition>
        {
            new() { Id = "word-doc", Enabled = true, Name = "Word", Description = "DOCX structure and styles" },
            new() { Id = "excel-x", Enabled = true, Name = "Excel", Description = "ranges" }
        });
        var hits = idx.Search("docx", 4);
        Assert.Contains(hits, e => string.Equals(e.SkillId, "word-doc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsAlphabeticalTopK()
    {
        var idx = SkillCatalogIndex.BuildFromEnabledSkills(new List<SkillDefinition>
        {
            new() { Id = "z", Enabled = true, Description = "z" },
            new() { Id = "a", Enabled = true, Description = "a" }
        });
        var hits = idx.Search("", 1);
        Assert.Single(hits);
        Assert.Equal("a", hits[0].SkillId, StringComparer.OrdinalIgnoreCase);
    }
}
