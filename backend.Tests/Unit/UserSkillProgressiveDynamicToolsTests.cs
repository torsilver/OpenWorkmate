using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWorkmate.Server.Plugins;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class UserSkillProgressiveDynamicToolsTests
{
    [Fact]
    public async Task LoadUserSkillInstructions_WithoutSelect_ReturnsBody()
    {
        var root = Path.Combine(Path.GetTempPath(), "owm_skill_load_" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "direct_load_skill");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: direct_load_skill
            description: direct load test
            ---

            expected body line
            """,
            Encoding.UTF8);

        var skillSvc = new SkillService(NullLogger<SkillService>.Instance, root);
        var plugin = new UserSkillProgressivePlugin(skillSvc, NullLogger<UserSkillProgressivePlugin>.Instance);

        var reg = new ToolRegistry();
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var skillCatalog = SkillCatalogIndex.BuildFromEnabledSkills(skillSvc.GetAllSkills());
        var cfg = new DynamicToolingConfig();
        var state = new DynamicToolingTurnState(cfg, catalog, skillCatalog);

        using (DynamicToolingTurnScope.Push(state))
        {
            var msg = await plugin.LoadUserSkillInstructionsAsync("direct_load_skill");
            Assert.Contains("expected body line", msg, StringComparison.Ordinal);
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore cleanup on locked temp (CI)
        }
    }

    [Fact]
    public async Task SearchAvailableSkills_RespectsFixedMaxPerTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "owm_skill_search_" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "s1");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: s1
            description: one
            ---

            b
            """,
            Encoding.UTF8);

        var skillSvc = new SkillService(NullLogger<SkillService>.Instance, root);
        var plugin = new UserSkillProgressivePlugin(skillSvc, NullLogger<UserSkillProgressivePlugin>.Instance);

        var reg = new ToolRegistry();
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var skillCatalog = SkillCatalogIndex.BuildFromEnabledSkills(skillSvc.GetAllSkills());
        var cfg = new DynamicToolingConfig();
        var state = new DynamicToolingTurnState(cfg, catalog, skillCatalog);

        using (DynamicToolingTurnScope.Push(state))
        {
            var max = DynamicToolingConstants.MaxSkillSearchPerTurnDefault;
            string? last = null;
            for (var i = 0; i < max; i++)
            {
                last = await plugin.SearchAvailableSkillsAsync("one");
                Assert.Contains("[search_available_skills]", last, StringComparison.Ordinal);
                Assert.DoesNotContain("已达本轮检索上限", last, StringComparison.Ordinal);
            }

            last = await plugin.SearchAvailableSkillsAsync("one");
            Assert.NotNull(last);
            Assert.Contains("已达本轮检索上限", last, StringComparison.Ordinal);
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
