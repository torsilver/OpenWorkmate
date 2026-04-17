using OfficeCopilot.Server.Services.DynamicTooling;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class DynamicToolingActivateRefreshTriggersTests
{
    [Theory]
    [InlineData("AgentTooling", "activate_tools", true)]
    [InlineData("agenttooling", "ACTIVATE_TOOLS", true)]
    [InlineData("AgentTooling", "ActivateToolsAsync", true)]
    [InlineData("AgentTooling", "activatetoolsasync", true)]
    [InlineData("OtherPlugin", "activate_tools", true)]
    [InlineData("OtherPlugin", "ActivateToolsAsync", false)]
    [InlineData("AgentTooling", "SearchAvailableToolsAsync", false)]
    [InlineData("", "activate_tools", true)]
    public void ShouldRefreshChatOptionsToolsAfterInvocation_matches_expected(string plugin, string func, bool expected)
    {
        var actual = DynamicToolingActivateRefreshTriggers.ShouldRefreshChatOptionsToolsAfterInvocation(plugin, func);
        Assert.Equal(expected, actual);
    }
}
