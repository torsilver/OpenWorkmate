using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OfficeCopilot.Server.Logging;
using OfficeCopilot.Server.Services.DashScope;

namespace OfficeCopilot.Server.Services;

/// <summary>内置完成度评判（无用户配置，使用主会话 <see cref="IChatClient"/>）。</summary>
public interface IBuiltinTurnCompletionVerifier
{
    Task<TurnCompletionEvaluationResult> EvaluateAsync(
        TurnCompletionVerifierRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>调用主模型前的结构化输入；不含 reasoning 流。</summary>
public sealed record TurnCompletionVerifierRequest(
    string UserRequest,
    string AssistantVisible,
    int SearchInvocationCount,
    int ActivateInvocationCount,
    IReadOnlyList<string> ActivatedBusinessToolNames);

/// <summary>解析成功且 <see cref="Incomplete"/> 为 true 时，外层可触发一轮 MAF 续跑。</summary>
public sealed record TurnCompletionEvaluationResult(bool Incomplete, string? Reason, bool ParseOk);

/// <summary>续跑前追加到 <see cref="ChatRole.User"/> 的内置提示（用户不可配置）。</summary>
public static class BuiltinTurnCompletionMessages
{
    public const string ContinuationUserNudge =
        "[系统续跑] 上一轮你在可见正文中尚未给出对用户有用的答复，或用户任务可能尚未完成。"
        + "请继续：必要时调用已激活的相关工具并依据工具返回作答；若任务确实无法继续，请用简短中文向用户说明原因与下一步建议。"
        + "请输出用户可见的正文，不要仅停留在内部思考。";

    public const string StreamWarningBeforeContinuation =
        "内置完成度评判认为本轮尚未充分完成用户请求，正在自动续跑一轮…";
}

/// <summary>解析评判模型输出中的 JSON（支持单行或 fenced 块）。</summary>
public static class BuiltinTurnCompletionVerdictParser
{
    /// <summary>
    /// 从模型原文中提取 <c>{"complete":bool,"reason":"..."}</c>。
    /// 解析失败或缺少 <c>complete</c> 时返回 <c>parseOk: false</c>，外层应视为不续跑。
    /// </summary>
    public static TurnCompletionEvaluationResult ParseModelOutput(string? raw)
    {
        var extracted = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(extracted))
            return new TurnCompletionEvaluationResult(Incomplete: false, Reason: null, ParseOk: false);

        try
        {
            using var doc = JsonDocument.Parse(extracted);
            var root = doc.RootElement;
            if (!root.TryGetProperty("complete", out var completeEl))
                return new TurnCompletionEvaluationResult(Incomplete: false, Reason: null, ParseOk: false);

            var complete = ReadCompleteBoolean(completeEl);
            if (complete is null)
                return new TurnCompletionEvaluationResult(Incomplete: false, Reason: null, ParseOk: false);

            var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()
                : null;

            return new TurnCompletionEvaluationResult(
                Incomplete: !complete.Value,
                Reason: reason,
                ParseOk: true);
        }
        catch (JsonException)
        {
            return new TurnCompletionEvaluationResult(Incomplete: false, Reason: null, ParseOk: false);
        }
    }

    private static bool? ReadCompleteBoolean(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => el.GetString() switch
            {
                "true" => true,
                "false" => false,
                _ => null
            },
            JsonValueKind.Number => el.TryGetInt64(out var n) ? n != 0 : null,
            _ => null
        };
    }

    /// <summary>去掉可选 markdown 代码围栏后取第一个 JSON 对象子串。</summary>
    internal static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl >= 0)
            {
                s = s[(firstNl + 1)..].TrimStart();
                var close = s.LastIndexOf("```", StringComparison.Ordinal);
                if (close >= 0)
                    s = s[..close].Trim();
            }
        }

        var start = s.IndexOf('{');
        if (start < 0)
            return null;
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return s.Substring(start, i - start + 1);
            }
        }

        return null;
    }
}

public sealed class BuiltinTurnCompletionVerifier : IBuiltinTurnCompletionVerifier
{
    public const int MaxUserRequestChars = 4096;
    public const int MaxAssistantVisibleChars = 8000;
    public const int MaxToolNamesInPayload = 64;
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(25);

    private static readonly string SystemPrompt =
        """
        你是对话完成度评判器（仅内部分析，用户不会直接看到你的输出）。
        下面 user 消息中包含三段：UserRequest（用户本轮要求）、AssistantVisible（助手已对用户可见的回复正文，可能为空）、ToolSummary（动态工具检索/激活的计数与已激活业务工具名列表）。
        请判断：仅根据这些材料，助手是否已经**充分完成**用户本轮提出的任务或给出了**对用户有用的可见答复**。

        规则：
        - 若 AssistantVisible 为空或仅有占位套话，且从 UserRequest 看仍需要工具执行或实质性答复，则 complete 应为 false。
        - 若用户只是闲聊、或 AssistantVisible 已合理回应用户请求，则 complete 应为 true。
        - 不要编造未出现在材料中的工具执行结果；不得引用「思考过程」字段（材料中不会提供）。
        - 只输出一个 JSON 对象，不要其它文字。格式严格为：
        {"complete":true或false,"reason":"一句中文简要理由"}
        """;

    private readonly IChatRuntimeAccessor _runtime;
    private readonly ILogger<BuiltinTurnCompletionVerifier> _logger;

    public BuiltinTurnCompletionVerifier(
        IChatRuntimeAccessor runtimeAccessor,
        ILogger<BuiltinTurnCompletionVerifier> logger)
    {
        _runtime = runtimeAccessor;
        _logger = logger;
    }

    public async Task<TurnCompletionEvaluationResult> EvaluateAsync(
        TurnCompletionVerifierRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = _runtime.GetChatClient();
        if (client == null)
        {
            _logger.LogDebug("BuiltinTurnCompletionVerifier: 无 IChatClient，跳过评判。");
            return new TurnCompletionEvaluationResult(false, null, false);
        }

        var userBlock = BuildUserPayload(request);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, userBlock)
        };

        using var timeoutCts = new CancellationTokenSource(EvaluationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var options = new ChatOptions
        {
            MaxOutputTokens = 384,
            Temperature = 0.1f
        };

        var responseText = new StringBuilder();
        try
        {
            using (DashScopeCallKindContext.EnterBackground())
            {
                var response = await client.GetResponseAsync(messages, options, linked.Token).ConfigureAwait(false);
                var t = response.Text ?? "";
                if (t.Length > 0)
                    responseText.Append(t);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("BuiltinTurnCompletionVerifier: 评判超时，跳过续跑。");
            return new TurnCompletionEvaluationResult(false, null, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuiltinTurnCompletionVerifier: 评判请求失败，跳过续跑。");
            return new TurnCompletionEvaluationResult(false, null, false);
        }

        var raw = HitlPlainLanguageExplainer.StripReasoningTags(responseText.ToString().Trim());
        var parsed = BuiltinTurnCompletionVerdictParser.ParseModelOutput(raw);
        var reasonPreview = parsed.Reason is { Length: > 0 } r
            ? LogPreview.HeadTail(r, 120, 40)
            : "";
        _logger.LogInformation(
            "BuiltinTurnCompletionVerifier: parseOk={ParseOk} incomplete={Incomplete} reasonPreview={ReasonPreview}",
            parsed.ParseOk,
            parsed.Incomplete,
            reasonPreview);
        return parsed;
    }

    private static string BuildUserPayload(TurnCompletionVerifierRequest request)
    {
        var ur = Truncate(request.UserRequest, MaxUserRequestChars);
        var av = Truncate(request.AssistantVisible, MaxAssistantVisibleChars);
        var names = request.ActivatedBusinessToolNames;
        var namePart = new StringBuilder();
        var n = Math.Min(names.Count, MaxToolNamesInPayload);
        for (var i = 0; i < n; i++)
        {
            if (i > 0) namePart.Append(',');
            namePart.Append(names[i]);
        }

        if (names.Count > MaxToolNamesInPayload)
            namePart.Append(",…");

        return $"""
            UserRequest:
            {ur}

            AssistantVisible:
            {av}

            ToolSummary:
            search_invocations={request.SearchInvocationCount}
            activate_invocations={request.ActivateInvocationCount}
            activated_business_tools={namePart}
            """;
    }

    private static string Truncate(string? s, int maxChars)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.Length <= maxChars)
            return s;
        return s[..maxChars] + "\n[...已截断]";
    }
}
