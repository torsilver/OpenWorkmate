using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace backend.Tests.Unit;

public sealed class DynamicToolingInstructionCopyTests
{
    [Fact]
    public void Text_PrescribesSkillsBeforeToolSearchWhenProgressiveSkillsPresent()
    {
        Assert.Contains("渐进式用户技能", DynamicToolingInstruction.Text, StringComparison.Ordinal);
        Assert.Contains("search_available_skills", DynamicToolingInstruction.Text, StringComparison.Ordinal);
        Assert.Contains("search_available_tools", DynamicToolingInstruction.Text, StringComparison.Ordinal);
        Assert.Contains("activate_tools", DynamicToolingInstruction.Text, StringComparison.Ordinal);
        Assert.Contains("门控", DynamicToolingInstruction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapDirectToolsHint_AlignsWithGate()
    {
        Assert.Contains("search_available_skills", DynamicToolingInstruction.BootstrapDirectToolsHint, StringComparison.Ordinal);
        Assert.Contains("search_available_tools", DynamicToolingInstruction.BootstrapDirectToolsHint, StringComparison.Ordinal);
        Assert.Contains("activate_tools", DynamicToolingInstruction.BootstrapDirectToolsHint, StringComparison.Ordinal);
    }
}
