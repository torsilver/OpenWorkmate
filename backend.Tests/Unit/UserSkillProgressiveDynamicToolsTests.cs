using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Plugins;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public class UserSkillProgressiveDynamicToolsTests
{
    [Fact]
    public async Task LoadUserSkillInstructions_WhenRequireSelectTrue_AndNotSelected_ReturnsGateMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "taskly_skill_gate_" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "load_gate_skill");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: load_gate_skill
            description: gate test
            ---

            body
            """,
            Encoding.UTF8);

        var skillSvc = new SkillService(NullLogger<SkillService>.Instance, root);
        var plugin = new UserSkillProgressivePlugin(skillSvc, NullLogger<UserSkillProgressivePlugin>.Instance);

        var reg = new ToolRegistry();
        var catalog = ToolCatalogIndex.BuildFromAllowedTools(reg, "chrome", null);
        var skillCatalog = SkillCatalogIndex.BuildFromEnabledSkills(skillSvc.GetAllSkills());
        var cfg = new DynamicToolingConfig { RequireSkillSelectBeforeLoad = true };
        var state = new DynamicToolingTurnState(cfg, catalog, skillCatalog);

        using (DynamicToolingTurnScope.Push(state))
        {
            var msg = await plugin.LoadUserSkillInstructionsAsync("load_gate_skill");
            Assert.Contains("select_skill_for_turn", msg, StringComparison.Ordinal);
            Assert.Contains("load_user_skill_instructions", msg, StringComparison.Ordinal);
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
    public async Task SearchAvailableSkills_RespectsMaxPerTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "taskly_skill_search_" + Guid.NewGuid().ToString("N"));
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
        var cfg = new DynamicToolingConfig { MaxSkillSearchPerTurn = 1 };
        var state = new DynamicToolingTurnState(cfg, catalog, skillCatalog);

        using (DynamicToolingTurnScope.Push(state))
        {
            var first = await plugin.SearchAvailableSkillsAsync("one");
            Assert.Contains("[search_available_skills]", first, StringComparison.Ordinal);
            var second = await plugin.SearchAvailableSkillsAsync("one");
            Assert.Contains("上限", second);
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
