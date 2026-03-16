using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class TokenEstimatorTests
{
    [Fact]
    public void EstimateTokens_NullText_ReturnsZero()
    {
        var result = TokenEstimator.EstimateTokens(null);
        Assert.Equal(0, result);
    }

    [Fact]
    public void EstimateTokens_EmptyText_ReturnsZero()
    {
        var result = TokenEstimator.EstimateTokens("");
        Assert.Equal(0, result);
    }

    [Fact]
    public void EstimateTokens_CharsRatio_DefaultCharsPerToken()
    {
        // 10 chars / 2 = 5 tokens
        var result = TokenEstimator.EstimateTokens("0123456789");
        Assert.Equal(5, result);
    }

    [Fact]
    public void EstimateTokens_CharsRatio_CustomCharsPerToken()
    {
        // 10 chars / 4 = 2.5 -> ceil = 3
        var result = TokenEstimator.EstimateTokens("0123456789", charsPerToken: 4);
        Assert.Equal(3, result);
    }

    [Fact]
    public void EstimateTokens_CharsRatio_OddCharsRoundsUp()
    {
        // 7 chars / 2 = 3.5 -> ceil = 4
        var result = TokenEstimator.EstimateTokens("abcdefg");
        Assert.Equal(4, result);
    }

    [Fact]
    public void EstimateTokens_Tiktoken_ProducesDifferentResultThanCharsRatio()
    {
        // "Hello world" - CharsRatio(2) = 6 tokens; Tiktoken typically ~2-3 for English
        var charsRatioResult = TokenEstimator.EstimateTokens("Hello world", "CharsRatio", 2);
        var tiktokenResult = TokenEstimator.EstimateTokens("Hello world", "Tiktoken", 2, "gpt-4o");
        Assert.NotEqual(charsRatioResult, tiktokenResult);
    }

    [Fact]
    public void EstimateTokens_Tiktoken_UnknownModel_FallsBackGracefully()
    {
        // Unknown model should fall back to cl100k_base, not throw
        var result = TokenEstimator.EstimateTokens("test text", "Tiktoken", 2, "unknown-model-xyz-12345");
        Assert.True(result >= 1);
    }

    [Fact]
    public void EstimateTokens_ContextWindowConfigOverload()
    {
        var config = new ContextWindowConfig
        {
            TokenEstimation = "CharsRatio",
            CharsPerToken = 5
        };
        // 10 chars / 5 = 2 tokens
        var result = TokenEstimator.EstimateTokens("0123456789", config);
        Assert.Equal(2, result);
    }

    [Fact]
    public void EstimateTokens_ContextWindowConfigOverload_WithModelId()
    {
        var config = new ContextWindowConfig
        {
            TokenEstimation = "Tiktoken",
            CharsPerToken = 2
        };
        var result = TokenEstimator.EstimateTokens("hello", config, "gpt-4o");
        Assert.True(result >= 1);
    }

    [Fact]
    public void EstimateImageTokens_ZeroByZero_ReturnsBase85()
    {
        var result = TokenEstimator.EstimateImageTokens(0, 0);
        Assert.Equal(85, result);
    }

    [Fact]
    public void EstimateImageTokens_NegativeDimensions_ReturnsBase85()
    {
        Assert.Equal(85, TokenEstimator.EstimateImageTokens(-1, 100));
        Assert.Equal(85, TokenEstimator.EstimateImageTokens(100, -1));
    }

    [Fact]
    public void EstimateImageTokens_ActualDimensions()
    {
        // 512x512 = 1 tile -> 85 + 170 = 255
        var result = TokenEstimator.EstimateImageTokens(512, 512);
        Assert.Equal(255, result);
    }

    [Fact]
    public void EstimateImageTokens_1024x1024_FourTiles()
    {
        // 1024x1024 = 2*2 tiles -> 85 + 4*170 = 765
        var result = TokenEstimator.EstimateImageTokens(1024, 1024);
        Assert.Equal(765, result);
    }

    [Fact]
    public void EstimateImageTokens_LargeImage()
    {
        // 4096x4096 = 8*8 = 64 tiles -> 85 + 64*170 = 10965
        var result = TokenEstimator.EstimateImageTokens(4096, 4096);
        Assert.Equal(10965, result);
    }
}
