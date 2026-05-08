using OpenWorkmate.Server.Plugins;
using OpenWorkmate.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class UserSkillProgressivePathTests
{
    [Fact]
    public void TryResolveSafeResourcePath_AllowsNestedFileUnderBaseDir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "OpenWorkmate-skill-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "references"));
        var relFile = Path.Combine(tmp, "references", "note.md");
        File.WriteAllText(relFile, "x");

        var skill = new SkillDefinition { Id = "t", BaseDir = tmp };
        Assert.True(UserSkillProgressivePlugin.TryResolveSafeResourcePath(skill, "references/note.md", out var full, out var err), err);
        Assert.Equal(Path.GetFullPath(relFile), Path.GetFullPath(full!), StringComparer.OrdinalIgnoreCase);

        Directory.Delete(tmp, true);
    }

    [Fact]
    public void TryResolveSafeResourcePath_RejectsParentTraversal()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "OpenWorkmate-skill-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var skill = new SkillDefinition { Id = "t", BaseDir = tmp };
        Assert.False(UserSkillProgressivePlugin.TryResolveSafeResourcePath(skill, "..\\Windows\\System.ini", out _, out var err));
        Assert.Contains("..", err, StringComparison.Ordinal);

        Directory.Delete(tmp, true);
    }

    [Fact]
    public void TryResolveSafeResourcePath_RejectsSkillMdViaRelativeArg()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "OpenWorkmate-skill-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "SKILL.md"), "---\nname: t\ndescription: d\n---\nbody");
        var skill = new SkillDefinition { Id = "t", BaseDir = tmp };
        Assert.False(UserSkillProgressivePlugin.TryResolveSafeResourcePath(skill, "SKILL.md", out _, out var err));
        Assert.Contains("relativeResourcePath", err, StringComparison.Ordinal);

        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ExtractMarkdownBodyAfterFrontmatterFromRaw_StripsYaml()
    {
        const string raw = "---\nname: a\ndescription: b\n---\n\nHello\n";
        var body = SkillService.ExtractMarkdownBodyAfterFrontmatterFromRaw(raw);
        Assert.Equal("Hello", body.Trim());
    }
}
