using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server.Services.OpenAiCompat;

/// <summary>
/// 联调 Moonshot / Kimi 等「Invalid request: text content is empty」时用的出站 messages 结构摘要（不落正文）。
/// </summary>
internal static class OpenAiReasoningEchoDiagnostics
{
    private const int MaxSummaryChars = 4000;

    /// <summary>记录出站请求体中 <c>messages</c> 的逐条索引摘要，便于对照上游 400。</summary>
    internal static void LogChatCompletionsMessagesOutline(
        ILogger? log,
        string phase,
        string modelEntryId,
        string? sessionId,
        ReadOnlySpan<byte> bodyUtf8)
    {
        if (log == null || bodyUtf8.IsEmpty)
            return;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bodyUtf8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;
            if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return;

            var summary = BuildMessagesSummary(messages);
            var model = "(absent)";
            if (doc.RootElement.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                model = modelEl.GetString() ?? "(null)";
            var streamVal = doc.RootElement.TryGetProperty("stream", out var se) && se.ValueKind == JsonValueKind.True;
            log.LogInformation(
                "[OpenAiReasoningEcho] {Phase} entry={Entry} session={Session} model={Model} stream={Stream} messageCount={Count} outline={Outline}",
                phase,
                modelEntryId,
                sessionId ?? "(null)",
                model,
                streamVal,
                messages.GetArrayLength(),
                summary);

            LogOutboundShapeStats(log, phase, modelEntryId, sessionId, model, messages);

            if (summary.Contains("WARN:", StringComparison.Ordinal)
                || summary.Contains("EMPTY_TX", StringComparison.Ordinal)
                || summary.Contains("(EMPTY_STR)", StringComparison.Ordinal))
            {
                log.LogWarning(
                    "[OpenAiReasoningEcho] suspicious_message_shape phase={Phase} entry={Entry} session={Session} model={Model} hint=check_empty_text_parts_or_assistant_content",
                    phase,
                    modelEntryId,
                    sessionId ?? "(null)",
                    model);
            }
        }
        catch (JsonException)
        {
            // 忽略无法解析的 body，避免干扰主路径
        }
    }

    private static void LogOutboundShapeStats(
        ILogger log,
        string phase,
        string modelEntryId,
        string? sessionId,
        string model,
        JsonElement messages)
    {
        var s = ComputeOutboundShapeStats(messages);
        log.LogInformation(
            "[OpenAiReasoningEcho] {Phase}_shape_stats entry={Entry} session={Session} model={Model} assistantToolMsgs={At} assistantToolMissingReasoning={MissRe} assistantToolEmptyStrContent={EmptyStr} msgsWithArrayEmptyTextPart={MsgArrEmpty} arrayEmptyTextPartSlots={ArrSlots} toolMsgs={Tool}",
            phase,
            modelEntryId,
            sessionId ?? "(null)",
            model,
            s.AssistantToolMessages,
            s.AssistantToolMissingReasoning,
            s.AssistantToolEmptyStringContent,
            s.MessagesWithArrayEmptyTextPart,
            s.ArrayEmptyTextPartSlots,
            s.ToolRoleMessages);

        if (s.ArrayEmptyTextPartSlots > 0 || s.AssistantToolEmptyStringContent > 0)
        {
            log.LogWarning(
                "[OpenAiReasoningEcho] kimi_text_empty_risk phase={Phase} entry={Entry} session={Session} arrayEmptyTextSlots={ArrSlots} assistantEmptyStr={EmptyStr} hint=moonshot_may_return_text_content_is_empty",
                phase,
                modelEntryId,
                sessionId ?? "(null)",
                s.ArrayEmptyTextPartSlots,
                s.AssistantToolEmptyStringContent);
        }

        if (s.AssistantToolMissingReasoning > 0)
        {
            log.LogDebug(
                "[OpenAiReasoningEcho] missing_reasoning_slots phase={Phase} entry={Entry} session={Session} count={Count} hint=patch_will_try_echo_store",
                phase,
                modelEntryId,
                sessionId ?? "(null)",
                s.AssistantToolMissingReasoning);
        }
    }

    private readonly struct OutboundShapeStats
    {
        internal readonly int AssistantToolMessages;
        internal readonly int AssistantToolMissingReasoning;
        internal readonly int AssistantToolEmptyStringContent;
        internal readonly int MessagesWithArrayEmptyTextPart;
        internal readonly int ArrayEmptyTextPartSlots;
        internal readonly int ToolRoleMessages;

        internal OutboundShapeStats(
            int assistantToolMessages,
            int assistantToolMissingReasoning,
            int assistantToolEmptyStringContent,
            int messagesWithArrayEmptyTextPart,
            int arrayEmptyTextPartSlots,
            int toolRoleMessages)
        {
            AssistantToolMessages = assistantToolMessages;
            AssistantToolMissingReasoning = assistantToolMissingReasoning;
            AssistantToolEmptyStringContent = assistantToolEmptyStringContent;
            MessagesWithArrayEmptyTextPart = messagesWithArrayEmptyTextPart;
            ArrayEmptyTextPartSlots = arrayEmptyTextPartSlots;
            ToolRoleMessages = toolRoleMessages;
        }
    }

    private static OutboundShapeStats ComputeOutboundShapeStats(JsonElement messages)
    {
        var assistantTool = 0;
        var missRe = 0;
        var emptyStr = 0;
        var msgsArrEmpty = 0;
        var arrSlots = 0;
        var toolMsgs = 0;

        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
                continue;

            var role = m.TryGetProperty("role", out var re) && re.ValueKind == JsonValueKind.String
                ? re.GetString()
                : null;
            if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                toolMsgs++;
                continue;
            }

            if (!IsAssistantWithNonEmptyToolCalls(m))
                continue;

            assistantTool++;
            if (IsAssistantToolCallsMissingReasoningForDiagnostics(m))
                missRe++;

            if (m.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(c.GetString()))
                    emptyStr++;
                else if (c.ValueKind == JsonValueKind.Array)
                {
                    var emptyParts = CountEmptyTextPartsInContentArray(c);
                    if (emptyParts > 0)
                    {
                        msgsArrEmpty++;
                        arrSlots += emptyParts;
                    }
                }
            }
        }

        return new OutboundShapeStats(assistantTool, missRe, emptyStr, msgsArrEmpty, arrSlots, toolMsgs);
    }

    private static bool IsAssistantWithNonEmptyToolCalls(JsonElement m)
    {
        if (!m.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
            return false;
        if (!string.Equals(roleEl.GetString(), "assistant", StringComparison.Ordinal))
            return false;
        if (!m.TryGetProperty("tool_calls", out var tc) || tc.ValueKind != JsonValueKind.Array || tc.GetArrayLength() == 0)
            return false;
        return true;
    }

    private static bool IsAssistantToolCallsMissingReasoningForDiagnostics(JsonElement m)
    {
        if (!IsAssistantWithNonEmptyToolCalls(m))
            return false;
        if (!m.TryGetProperty("reasoning_content", out var rc))
            return true;
        if (rc.ValueKind == JsonValueKind.Null)
            return true;
        return rc.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(rc.GetString());
    }

    private static int CountEmptyTextPartsInContentArray(JsonElement contentArray)
    {
        var n = 0;
        foreach (var part in contentArray.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;
            if (!part.TryGetProperty("text", out var tx) || tx.ValueKind != JsonValueKind.String)
                continue;
            if (string.IsNullOrEmpty(tx.GetString()))
                n++;
        }

        return n;
    }

    internal static string BuildMessagesSummary(JsonElement messages)
    {
        var sb = new StringBuilder(Math.Min(512, MaxSummaryChars));
        var i = 0;
        foreach (var m in messages.EnumerateArray())
        {
            if (i > 0)
                sb.Append(" | ");
            AppendMessageSlot(sb, i, m);
            i++;
            if (sb.Length >= MaxSummaryChars - 8)
            {
                sb.Append(" |...(truncated)");
                break;
            }
        }

        return sb.ToString();
    }

    private static void AppendMessageSlot(StringBuilder sb, int idx, JsonElement m)
    {
        sb.Append('[').Append(idx).Append(']');
        var role = m.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
            ? roleEl.GetString() ?? "?"
            : "?";
        sb.Append(role);

        if (!m.TryGetProperty("content", out var content))
            sb.Append(",content=(absent)");
        else
            DescribeContent(sb, content);

        if (m.TryGetProperty("tool_calls", out var tc)
            && tc.ValueKind == JsonValueKind.Array
            && tc.GetArrayLength() > 0)
            sb.Append(",tools=").Append(tc.GetArrayLength());

        if (m.TryGetProperty("reasoning_content", out var rc))
        {
            if (rc.ValueKind == JsonValueKind.String)
                sb.Append(",reasoningLen=").Append((rc.GetString() ?? "").Length);
            else if (rc.ValueKind == JsonValueKind.Null)
                sb.Append(",reasoning=null");
        }

        if (string.Equals(role, "assistant", StringComparison.Ordinal)
            && m.TryGetProperty("tool_calls", out var tc2)
            && tc2.ValueKind == JsonValueKind.Array
            && tc2.GetArrayLength() > 0
            && m.TryGetProperty("content", out var ac))
        {
            if (ac.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(ac.GetString()))
                sb.Append(",WARN:assistantToolsEmptyText");
            else if (ac.ValueKind == JsonValueKind.Array && ContentArrayHasEmptyTextPart(ac))
                sb.Append(",WARN:assistantToolsEmptyTextPart");
        }
    }

    private static void DescribeContent(StringBuilder sb, JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.Null:
                sb.Append(",content=null");
                break;
            case JsonValueKind.String:
                {
                    var t = content.GetString() ?? "";
                    sb.Append(",textLen=").Append(t.Length);
                    if (t.Length == 0)
                        sb.Append("(EMPTY_STR)");
                    break;
                }
            case JsonValueKind.Array:
                {
                    var n = content.GetArrayLength();
                    sb.Append(",contentParts=").Append(n);
                    AppendContentPartsDetail(sb, content);
                    break;
                }
            default:
                sb.Append(",contentKind=").Append(content.ValueKind);
                break;
        }
    }

    /// <summary>数组型 content：逐段 type / text 长度（不落正文），便于对照 Moonshot「text content is empty」。</summary>
    private static void AppendContentPartsDetail(StringBuilder sb, JsonElement contentArray)
    {
        sb.Append(",parts=");
        var i = 0;
        const int maxParts = 12;
        foreach (var part in contentArray.EnumerateArray())
        {
            if (i >= maxParts)
            {
                sb.Append("|…");
                break;
            }

            if (i > 0)
                sb.Append('|');

            if (part.ValueKind != JsonValueKind.Object)
            {
                sb.Append('[').Append(part.ValueKind).Append(']');
                i++;
                continue;
            }

            var partType = "(?)";
            if (part.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String)
                partType = tp.GetString() ?? "(?)";

            var textLen = -1;
            if (part.TryGetProperty("text", out var tx))
            {
                if (tx.ValueKind == JsonValueKind.String)
                    textLen = (tx.GetString() ?? "").Length;
                else if (tx.ValueKind == JsonValueKind.Null)
                    textLen = -2;
            }

            sb.Append('[').Append(i).Append(':').Append(partType);
            if (textLen >= 0)
            {
                sb.Append(",tLen=").Append(textLen);
                if (textLen == 0)
                    sb.Append(",EMPTY_TX");
            }
            else if (textLen == -2)
                sb.Append(",text=null");

            sb.Append(']');
            i++;
        }
    }

    private static bool ContentArrayHasEmptyTextPart(JsonElement contentArray)
    {
        foreach (var part in contentArray.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;
            if (!part.TryGetProperty("text", out var tx) || tx.ValueKind != JsonValueKind.String)
                continue;
            if (string.IsNullOrEmpty(tx.GetString()))
                return true;
        }

        return false;
    }
}
