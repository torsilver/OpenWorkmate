using System.Collections.Concurrent;
using SharpToken;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

/// <summary>根据配置估算文本 token 数，用于上下文窗口预算（CharsRatio 或 Tiktoken）。</summary>
public static class TokenEstimator
{
    private static readonly ConcurrentDictionary<string, GptEncoding> _encodingCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按配置估算字符串的 token 数。TokenEstimation=Tiktoken 时用 SharpToken 精确编码；CharsRatio 时用 charsPerToken 粗估。</summary>
    public static int EstimateTokens(string? text, string tokenEstimation = "CharsRatio", int charsPerToken = 2, string? modelId = null)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (string.Equals(tokenEstimation, "Tiktoken", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoding = GetEncoding(modelId);
                return encoding.Encode(text).Count;
            }
            catch
            {
                // SharpToken 不认识该模型时回退 CharsRatio
            }
        }
        var chars = text.Length;
        var ratio = Math.Max(1, charsPerToken);
        return (int)Math.Ceiling((double)chars / ratio);
    }

    /// <summary>使用 ContextWindowConfig 估算 token 数。</summary>
    public static int EstimateTokens(string? text, ContextWindowConfig config, string? modelId = null)
    {
        return EstimateTokens(text, config.TokenEstimation, config.CharsPerToken, modelId);
    }

    /// <summary>视觉图片 token 估算（OpenAI 规则：每 512x512 tile 约 85 tokens，基础 85）。</summary>
    public static int EstimateImageTokens(int width, int height)
    {
        if (width <= 0 || height <= 0) return 85;
        var tiles = (int)Math.Ceiling((double)width / 512) * (int)Math.Ceiling((double)height / 512);
        return 85 + tiles * 170;
    }

    private static GptEncoding GetEncoding(string? modelId)
    {
        var key = string.IsNullOrWhiteSpace(modelId) ? "cl100k_base" : modelId;
        return _encodingCache.GetOrAdd(key, k =>
        {
            try { return GptEncoding.GetEncodingForModel(k); }
            catch { return GptEncoding.GetEncoding("cl100k_base"); }
        });
    }
}
