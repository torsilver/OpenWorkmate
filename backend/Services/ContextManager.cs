using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 上下文管理：历史裁剪、截断、摘要、注入等逻辑的统一入口。
/// 从 ChatService 中提取出来，便于独立测试和复用。
/// </summary>
public sealed class ContextManager
{
    private readonly ConfigService _configService;
    private readonly ILogger<ContextManager> _logger;

    public ContextManager(ConfigService configService, ILogger<ContextManager> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>获取当前生效的上下文 token 上限。</summary>
    public int GetEffectiveMaxContextTokens(AiModelEntry? activeEntry)
    {
        if (activeEntry?.ContextLength is > 0)
            return activeEntry.ContextLength.Value;
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        return ctx.MaxContextTokens > 0 ? ctx.MaxContextTokens : 64_000;
    }

    /// <summary>按轮数和 token 预算裁剪历史。</summary>
    public void TrimHistory(ChatHistory history, AiModelEntry? activeEntry)
    {
        var session = _configService.Current.Session ?? new SessionConfig();
        var ctx = _configService.Current.ContextWindow ?? new ContextWindowConfig();
        var maxMessagesByTurns = 1 + session.MaxHistoryTurns * 2;

        while (history.Count > maxMessagesByTurns)
            history.RemoveAt(1);

        if (ctx.PassThroughContext)
            return;

        var maxContextTokens = GetEffectiveMaxContextTokens(activeEntry);
        if (maxContextTokens <= 0)
            return;

        var budget = maxContextTokens - ctx.ReservedOutputTokens;
        if (budget <= 0)
            return;

        var totalTokens = EstimateHistoryTokens(history, ctx);
        var minMessagesToKeep = 1 + Math.Max(0, session.MinTurnsToKeep) * 2;
        while (totalTokens > budget && history.Count > minMessagesToKeep)
        {
            if (history.Count <= 2)
                break;
            var removed = EstimateMessageTokens(history[1], ctx) + (history.Count > 2 ? EstimateMessageTokens(history[2], ctx) : 0);
            history.RemoveAt(1);
            if (history.Count > 1)
                history.RemoveAt(1);
            totalTokens -= removed;
        }
    }

    /// <summary>估算整个历史的 token 总数，含 ImageContent 的视觉 token 估算。</summary>
    public static int EstimateHistoryTokens(ChatHistory history, ContextWindowConfig ctx)
    {
        var total = 0;
        for (var i = 0; i < history.Count; i++)
            total += EstimateMessageTokens(history[i], ctx);
        return total;
    }

    /// <summary>估算单条消息的 token 数，含 ImageContent 的视觉 token 估算。</summary>
    public static int EstimateMessageTokens(ChatMessageContent msg, ContextWindowConfig ctx)
    {
        var tokens = TokenEstimator.EstimateTokens(msg.Content ?? "", ctx);
        if (msg.Items is { Count: > 0 })
        {
            foreach (var item in msg.Items)
            {
                if (item is Microsoft.SemanticKernel.TextContent text)
                    tokens += TokenEstimator.EstimateTokens(text.Text ?? "", ctx);
                else if (item is Microsoft.SemanticKernel.ImageContent)
                    tokens += TokenEstimator.EstimateImageTokens(1024, 1024);
            }
        }
        return tokens;
    }

    /// <summary>为 context_length 重试裁剪历史：先按轮数限制，再按预算减半裁剪。</summary>
    public static void TrimHistoryForRetry(ChatHistory history, int maxTurns, ContextWindowConfig ctx)
    {
        var keepMessages = 1 + Math.Max(0, maxTurns) * 2;
        while (history.Count > keepMessages)
            history.RemoveAt(1);
        var halfBudget = (ctx.MaxContextTokens - ctx.ReservedOutputTokens) / 2;
        if (halfBudget <= 0) return;
        var total = 0;
        for (var i = 0; i < history.Count; i++)
            total += TokenEstimator.EstimateTokens(history[i].Content ?? "", ctx);
        while (total > halfBudget && history.Count > 3)
        {
            var removed = TokenEstimator.EstimateTokens(history[1].Content ?? "", ctx);
            history.RemoveAt(1);
            total -= removed;
        }
    }

    /// <summary>执行摘要压缩核心逻辑。</summary>
    public static async Task<(bool DidCompact, int TurnsSummarized)> SummarizeOldTurnsCoreAsync(
        ChatHistory history, Kernel kernel, IChatCompletionService chatService,
        ContextWindowConfig ctx, string sessionId, string? offloadDirectory, CancellationToken ct)
    {
        const int maxTurnsToSummarize = 6;
        var toTake = Math.Min(maxTurnsToSummarize * 2, history.Count - 1);
        if (toTake < 4)
            return (false, 0);
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= toTake && i < history.Count; i++)
        {
            var msg = history[i];
            var role = msg.Role.Label ?? msg.Role.ToString() ?? "unknown";
            var content = msg.Content ?? "";
            sb.AppendLine($"[{role}] {content}");
        }
        var input = sb.ToString().Trim();
        if (input.Length == 0)
            return (false, 0);

        if (!string.IsNullOrEmpty(offloadDirectory))
        {
            try
            {
                Directory.CreateDirectory(offloadDirectory);
                var safeName = string.Join("_", (sessionId ?? "").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                if (string.IsNullOrEmpty(safeName)) safeName = "session";
                var path = Path.Combine(offloadDirectory, safeName + ".md");
                var section = $"\n\n## Summarized at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n\n{input}\n\n";
                await File.AppendAllTextAsync(path, section, ct).ConfigureAwait(false);
            }
            catch { /* offload best-effort */ }
        }

        var maxChars = Math.Max(100, Math.Min(ctx.SummarizationMaxSummaryChars, 2000));
        var systemPrompt = $"你是一个对话摘要助手。请将以下对话压缩为一段简短摘要，保留关键事实与结论。摘要不超过 {maxChars} 字。只输出摘要正文，不要输出「摘要：」等前缀。";
        var summaryHistory = new ChatHistory(systemPrompt);
        summaryHistory.AddUserMessage(input);
        var settings = new OpenAIPromptExecutionSettings { MaxTokens = 800, Temperature = 0.2f };
        var summaryBuilder = new System.Text.StringBuilder();
        await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(summaryHistory, settings, kernel, ct).ConfigureAwait(false))
        {
            if (chunk.Content is { Length: > 0 } text)
                summaryBuilder.Append(text);
        }
        var summary = summaryBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(summary))
            return (false, 0);
        if (summary.Length > ctx.SummarizationMaxSummaryChars)
            summary = summary.AsSpan(0, ctx.SummarizationMaxSummaryChars).ToString() + "…";
        for (var i = 0; i < toTake; i++)
            history.RemoveAt(1);
        history.Insert(1, new ChatMessageContent(AuthorRole.User, "[此前对话摘要]\n" + summary));
        return (true, toTake / 2);
    }

    public static bool IsContextLengthError(Exception ex)
    {
        var msg = (ex.Message ?? "").ToLowerInvariant();
        return msg.Contains("context_length") || msg.Contains("maximum context") || msg.Contains("token limit")
            || msg.Contains("too many tokens");
    }
}
