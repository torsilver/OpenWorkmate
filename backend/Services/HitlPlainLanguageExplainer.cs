using System.Text;
using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Services.DashScope;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// HITL 确认前：用主模型、无会话历史、无工具，将待执行的命令/脚本原文概括为简短中文说明。
/// </summary>
public interface IHitlPlainLanguageExplainer
{
    Task<string?> SummarizeAsync(string rawExecutableText, CancellationToken cancellationToken = default);
}

public sealed class HitlPlainLanguageExplainer : IHitlPlainLanguageExplainer
{
    public const int DefaultMaxRawChars = 12000;
    private const string TruncationMarker = "\n\n[...已截断]";
    private static readonly TimeSpan SummarizeTimeout = TimeSpan.FromSeconds(10);

    private static readonly string SystemPrompt =
        """
        下一条 user 消息仅为「将在本机执行的命令或脚本」的原文，可能对应 shell、PowerShell、浏览器页面脚本、文档宿主脚本等。请根据字面内容判断类型。
        请用简体中文简要说明这段内容**在做什么**（2～4 句或一段极短话，面向不懂技术的人）。
        不要建议用户是否允许执行，不要下安全结论（如「无害」「安全」）。
        不要使用 Markdown 标题或列表层级；直接输出说明正文。
        若文末出现标记「[...已截断]」，表示原文已被截断，请基于可见部分概括，并可提示「内容不完整」。
        不要输出思考过程或 think、thinking 等 XML 式标签。
        """;

    private readonly IChatRuntimeAccessor _runtime;
    private readonly ILogger<HitlPlainLanguageExplainer> _logger;

    public HitlPlainLanguageExplainer(IChatRuntimeAccessor runtimeAccessor, ILogger<HitlPlainLanguageExplainer> logger)
    {
        _runtime = runtimeAccessor;
        _logger = logger;
    }

    /// <summary>
    /// 与下发给前端的 HITL「原文」及传给模型的 user 内容使用同一截断规则。
    /// </summary>
    public static string TruncateRawExecutable(string? raw, int maxChars = DefaultMaxRawChars)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        if (raw.Length <= maxChars)
            return raw;
        return raw[..maxChars] + TruncationMarker;
    }

    public async Task<string?> SummarizeAsync(string rawExecutableText, CancellationToken cancellationToken = default)
    {
        var trimmed = rawExecutableText?.Trim() ?? "";
        if (trimmed.Length == 0)
            return null;

        var client = _runtime.GetChatClient();
        if (client == null)
        {
            _logger.LogDebug("HitlPlainLanguage: 无 IChatClient，跳过概括。");
            return null;
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, trimmed)
        };

        using var timeoutCts = new CancellationTokenSource(SummarizeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var options = new ChatOptions { MaxOutputTokens = 384, Temperature = 0.15f };
        var responseText = new StringBuilder();
        try
        {
            using (DashScopeCallKindContext.EnterBackground())
            {
                await foreach (var update in client
                                   .GetStreamingResponseAsync(messages, options, linked.Token)
                                   .ConfigureAwait(false))
                {
                    if (update.Text is { Length: > 0 } content)
                        responseText.Append(content);
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("HitlPlainLanguage: 概括超时，已跳过。");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HitlPlainLanguage: 概括失败，已跳过。");
            return null;
        }

        var raw = StripReasoningTags(responseText.ToString().Trim());
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    internal static string StripReasoningTags(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var s = text;
        foreach (var (open, close) in ReasoningTagStreamParser.TagPairs)
        {
            while (true)
            {
                var idx = s.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    break;
                var end = s.IndexOf(close, idx + open.Length, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    s = s.Remove(idx, open.Length);
                    continue;
                }

                s = s.Remove(idx, end + close.Length - idx);
            }
        }

        return s.Trim();
    }
}
