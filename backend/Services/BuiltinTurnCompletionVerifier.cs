using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenWorkmate.Server.Logging;
using OpenWorkmate.Server.Services.DashScope;

namespace OpenWorkmate.Server.Services;

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

/// <summary>内置评判结构化输出：<see cref="NeedMoreWork"/> 可触发 MAF 续跑；<see cref="AskUser"/> 由外层生成追问句。</summary>
public enum TurnCompletionVerifierOutcome
{
    Unknown,
    Done,
    NeedMoreWork,
    AskUser,
}

/// <param name="Outcome"><see cref="TurnCompletionVerifierOutcome.Unknown"/> 表示解析失败或未调用评判。</param>
public sealed record TurnCompletionEvaluationResult(
    TurnCompletionVerifierOutcome Outcome,
    string? Reason,
    bool ParseOk)
{
    /// <summary>兼容旧逻辑：仅 <see cref="TurnCompletionVerifierOutcome.NeedMoreWork"/> 视为需 MAF 续跑。</summary>
    public bool Incomplete => ParseOk && Outcome == TurnCompletionVerifierOutcome.NeedMoreWork;
}

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
    /// 优先解析 <c>{"outcome":"done|need_more_work|ask_user","reason":"..."}</c>；
    /// 否则兼容 <c>{"complete":bool,"reason":"..."}</c>（complete true→done，false→need_more_work）。
    /// </summary>
    public static TurnCompletionEvaluationResult ParseModelOutput(string? raw)
    {
        var extracted = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(extracted))
            return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, Reason: null, ParseOk: false);

        try
        {
            using var doc = JsonDocument.Parse(extracted);
            var root = doc.RootElement;
            var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()
                : null;

            if (root.TryGetProperty("outcome", out var outcomeEl) && outcomeEl.ValueKind == JsonValueKind.String)
            {
                var o = MapOutcomeString(outcomeEl.GetString());
                if (o == TurnCompletionVerifierOutcome.Unknown)
                    return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, reason, ParseOk: false);
                return new TurnCompletionEvaluationResult(o, reason, ParseOk: true);
            }

            if (!root.TryGetProperty("complete", out var completeEl))
                return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, reason, ParseOk: false);

            var complete = ReadCompleteBoolean(completeEl);
            if (complete is null)
                return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, reason, ParseOk: false);

            var legacy = complete.Value ? TurnCompletionVerifierOutcome.Done : TurnCompletionVerifierOutcome.NeedMoreWork;
            return new TurnCompletionEvaluationResult(legacy, reason, ParseOk: true);
        }
        catch (JsonException)
        {
            return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, Reason: null, ParseOk: false);
        }
    }

    private static TurnCompletionVerifierOutcome MapOutcomeString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return TurnCompletionVerifierOutcome.Unknown;
        if (string.Equals(s, "done", StringComparison.OrdinalIgnoreCase))
            return TurnCompletionVerifierOutcome.Done;
        if (string.Equals(s, "need_more_work", StringComparison.OrdinalIgnoreCase))
            return TurnCompletionVerifierOutcome.NeedMoreWork;
        if (string.Equals(s, "ask_user", StringComparison.OrdinalIgnoreCase))
            return TurnCompletionVerifierOutcome.AskUser;
        return TurnCompletionVerifierOutcome.Unknown;
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
        请判断：仅根据这些材料，本轮是否应结束、是否应续跑以完成任务，或是否信息不足应先向用户追问。

        规则：
        - outcome=done：用户只是闲聊/试探、或 AssistantVisible 已合理回应、或任务已充分完成。
        - outcome=need_more_work：需要工具执行或更实质的可见答复，但 AssistantVisible 仍不足（空、占位、未完成）。
        - outcome=ask_user：用户意图不清、缺少关键信息，应先让用户补充后再继续（不要选 need_more_work 代替追问）。
        - 不要编造未出现在材料中的工具执行结果。
        - 只输出一个 JSON 对象，不要其它文字。格式严格为：
        {"outcome":"done"|"need_more_work"|"ask_user","reason":"一句中文简要理由"}
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
            return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, null, false);
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
            return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, null, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuiltinTurnCompletionVerifier: 评判请求失败，跳过续跑。");
            return new TurnCompletionEvaluationResult(TurnCompletionVerifierOutcome.Unknown, null, false);
        }

        var raw = HitlPlainLanguageExplainer.StripReasoningTags(responseText.ToString().Trim());
        var parsed = BuiltinTurnCompletionVerdictParser.ParseModelOutput(raw);
        var reasonPreview = parsed.Reason is { Length: > 0 } r
            ? LogPreview.HeadTail(r, 120, 40)
            : "";
        _logger.LogInformation(
            "BuiltinTurnCompletionVerifier: parseOk={ParseOk} outcome={Outcome} incomplete={Incomplete} reasonPreview={ReasonPreview}",
            parsed.ParseOk,
            parsed.Outcome,
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
