using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

/// <summary>根据配置估算文本 token 数，用于上下文窗口预算（CharsRatio 或后续 Tiktoken）。</summary>
public static class TokenEstimator
{
    /// <summary>按配置估算字符串的 token 数。TokenEstimation=CharsRatio 时用 charsPerToken；为 Tiktoken 时暂回退 CharsRatio。</summary>
    public static int EstimateTokens(string? text, string tokenEstimation = "CharsRatio", int charsPerToken = 2)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (string.Equals(tokenEstimation, "Tiktoken", StringComparison.OrdinalIgnoreCase))
        {
            // 预留：可接入 TiktokenSharp/SharpToken，当前回退 CharsRatio
        }
        var chars = text.Length;
        var ratio = Math.Max(1, charsPerToken);
        return (int)Math.Ceiling((double)chars / ratio);
    }

    /// <summary>使用 ContextWindowConfig 估算 token 数。</summary>
    public static int EstimateTokens(string? text, ContextWindowConfig config)
    {
        return EstimateTokens(text, config.TokenEstimation, config.CharsPerToken);
    }
}
