using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Services;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class SystemPromptBuilderTests
{
    [Fact]
    public void BuildAugmentedSystemTextForStreaming_AppendsBlocksInOrder()
    {
        var baseText = "BASE";
        var outText = SystemPromptBuilder.BuildAugmentedSystemTextForStreaming(baseText, enableSearchSuppressionSuffix: null);
        Assert.StartsWith("BASE", outText);
        Assert.Contains(SystemPromptBuilder.LatestIntentAndGroundedFactsInstruction, outText);
        Assert.Contains(SystemPromptBuilder.ToolCallArgumentsSchemaInstruction, outText);
        Assert.Contains(SystemPromptBuilder.ToolResultEchoSystemInstruction, outText);
        var idxIntent = outText.IndexOf("[意图优先级]", StringComparison.Ordinal);
        var idxToolArgs = outText.IndexOf("[工具调用参数]", StringComparison.Ordinal);
        var idxEcho = outText.IndexOf("[工具与回答方式]", StringComparison.Ordinal);
        Assert.True(idxIntent < idxToolArgs && idxToolArgs < idxEcho);
    }

    [Fact]
    public void BuildAugmentedSystemTextForStreaming_InsertsEnableSearchBeforeIntentBlock()
    {
        var sup = SystemPromptBuilder.EnableSearchSuppressionInstruction;
        var outText = SystemPromptBuilder.BuildAugmentedSystemTextForStreaming("X", sup);
        var idxSup = outText.IndexOf("[联网检索]", StringComparison.Ordinal);
        var idxIntent = outText.IndexOf("[意图优先级]", StringComparison.Ordinal);
        Assert.True(idxSup < idxIntent);
    }

    [Fact]
    public void BuildHistoryForStreamingTurn_WithSystem_AppendsFixedBlocks()
    {
        var hist = new List<ChatMessage>
        {
            new(ChatRole.System, "SYS"),
            new(ChatRole.User, "hi"),
        };
        var built = SystemPromptBuilder.BuildHistoryForStreamingTurn(hist, identitySuffix: null, enableSearchSuppressionSuffix: null);
        Assert.Equal(2, built.Count);
        Assert.Equal(ChatRole.System, built[0].Role);
        var t = built[0].Text ?? "";
        Assert.StartsWith("SYS", t);
        Assert.Contains("[意图优先级]", t);
    }

    [Fact]
    public void BuildHistoryForStreamingTurn_IdentitySuffixPrependedBeforeAugmentation()
    {
        var hist = new List<ChatMessage> { new(ChatRole.System, "SYS"), new(ChatRole.User, "u") };
        var built = SystemPromptBuilder.BuildHistoryForStreamingTurn(hist, identitySuffix: "ID", enableSearchSuppressionSuffix: null);
        var t = built[0].Text ?? "";
        Assert.Contains("SYS", t);
        Assert.Contains("ID", t);
        Assert.Contains("[意图优先级]", t);
    }

    [Fact]
    public void GetClientTypeIdentitySuffix_Chrome_IsNonEmpty()
    {
        var s = SystemPromptBuilder.GetClientTypeIdentitySuffix("chrome");
        Assert.False(string.IsNullOrEmpty(s));
        Assert.Contains("侧边栏", s);
    }

    [Fact]
    public void GetClientTypeIdentitySuffix_Unknown_IsEmpty()
    {
        Assert.Equal("", SystemPromptBuilder.GetClientTypeIdentitySuffix("unknown-client"));
    }
}
