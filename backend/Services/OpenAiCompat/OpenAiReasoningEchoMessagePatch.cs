using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenWorkmate.Server.Services.ModelProfiles;

namespace OpenWorkmate.Server.Services.OpenAiCompat;

/// <summary>
/// 为 OpenAI 兼容 <c>messages</c> 中「assistant + tool_calls 且缺 <c>reasoning_content</c>」的条目按轮次注入会话内缓存的推理全文；
/// 去掉 assistant 的 multipart <c>content</c> 中空 <c>text</c> 段（Kimi/Moonshot 拒收）；可选合并 Kimi <c>thinking.keep</c>。
/// </summary>
internal static class OpenAiReasoningEchoMessagePatch
{
    private const string EmptyContentPlaceholder = " ";

    /// <summary>若无需改写则返回 null。</summary>
    internal static byte[]? TryPatchRequestUtf8(
        ReadOnlySpan<byte> bodyUtf8,
        string? sessionId,
        MergedModelProfile? profile,
        ILogger? log)
    {
        if (bodyUtf8.IsEmpty || string.IsNullOrEmpty(sessionId))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bodyUtf8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var root = doc.RootElement;
            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return null;

            var needSanitize = MessagesNeedContentSanitize(messages);
            var missingCount = CountAssistantToolCallsMissingReasoning(messages);
            var needThinkingKeep = profile is { UseThinkingKeepAll: true };

            if (!needSanitize && missingCount == 0 && !needThinkingKeep)
                return null;

            var reasonings = OpenAiReasoningEchoStore.SnapshotReasonings(sessionId);

            if (missingCount > 0
                && profile is { RequiresReasoningEchoWithTools: true }
                && InsufficientReasoningInStore(messages, reasonings))
            {
                log?.LogWarning(
                    "[OpenAiReasoningEcho] session={Session}: assistant+tool_calls missing reasoning_content but echo store had insufficient rounds for ordinal mapping.",
                    sessionId);
            }

            if (missingCount == 0 && !needSanitize && needThinkingKeep)
            {
                var tkOnly = MergeThinkingKeepOnly(root, profile);
                if (tkOnly != null)
                {
                    log?.LogInformation(
                        "[OpenAiReasoningEcho] patch thinking.keep only session={Session} profile={Profile} bytesIn={In} bytesOut={Out}",
                        sessionId,
                        profile?.ProfileKey ?? "(null)",
                        bodyUtf8.Length,
                        tkOnly.Length);
                }

                return tkOnly;
            }

            var merged = RewriteRootUnified(root, reasonings, profile, out var injectedSlots, out var strippedEmptyTextParts);
            log?.LogInformation(
                "[OpenAiReasoningEcho] patch reasoning echo session={Session} profile={Profile} missingSlots={Missing} injectedSlots={Injected} storeRounds={StoreRounds} strippedEmptyTextParts={Stripped} thinkingKeep={Keep} bytesIn={In} bytesOut={Out}",
                sessionId,
                profile?.ProfileKey ?? "(null)",
                missingCount,
                injectedSlots,
                reasonings.Count,
                strippedEmptyTextParts,
                profile is { UseThinkingKeepAll: true },
                bodyUtf8.Length,
                merged.Length);
            return merged;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool MessagesNeedContentSanitize(JsonElement messages)
    {
        foreach (var m in messages.EnumerateArray())
        {
            if (!IsAssistantRole(m))
                continue;
            if (AssistantMessageNeedsContentSanitize(m))
                return true;
        }

        return false;
    }

    private static bool IsAssistantRole(JsonElement m)
    {
        return m.ValueKind == JsonValueKind.Object
               && m.TryGetProperty("role", out var roleEl)
               && roleEl.ValueKind == JsonValueKind.String
               && string.Equals(roleEl.GetString(), "assistant", StringComparison.Ordinal);
    }

    /// <summary>assistant：空字符串 content，或数组中含空 text 段。</summary>
    private static bool AssistantMessageNeedsContentSanitize(JsonElement m)
    {
        if (!m.TryGetProperty("content", out var c))
            return false;

        switch (c.ValueKind)
        {
            case JsonValueKind.String:
                return string.IsNullOrEmpty(c.GetString());
            case JsonValueKind.Array:
                return ContentArrayHasEmptyTextPart(c);
            default:
                return false;
        }
    }

    private static bool ContentArrayHasEmptyTextPart(JsonElement contentArray)
    {
        foreach (var part in contentArray.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;
            if (!part.TryGetProperty("type", out var tp) || tp.ValueKind != JsonValueKind.String)
                continue;
            if (!string.Equals(tp.GetString(), "text", StringComparison.Ordinal))
                continue;
            if (!part.TryGetProperty("text", out var tx))
                return true;
            if (tx.ValueKind == JsonValueKind.Null)
                return true;
            if (tx.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(tx.GetString()))
                return true;
        }

        return false;
    }

    private static bool InsufficientReasoningInStore(JsonElement messages, IReadOnlyList<string> reasonings)
    {
        var assistantToolOrdinal = 0;
        foreach (var m in messages.EnumerateArray())
        {
            if (!IsAssistantWithNonEmptyToolCalls(m))
                continue;
            if (IsAssistantToolCallsMissingReasoning(m) && assistantToolOrdinal >= reasonings.Count)
                return true;
            assistantToolOrdinal++;
        }

        return false;
    }

    private static int CountAssistantToolCallsMissingReasoning(JsonElement messages)
    {
        var n = 0;
        foreach (var m in messages.EnumerateArray())
        {
            if (IsAssistantToolCallsMissingReasoning(m))
                n++;
        }

        return n;
    }

    private static bool IsAssistantWithNonEmptyToolCalls(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object)
            return false;
        if (!m.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
            return false;
        if (!string.Equals(roleEl.GetString(), "assistant", StringComparison.Ordinal))
            return false;
        if (!m.TryGetProperty("tool_calls", out var tc) || tc.ValueKind != JsonValueKind.Array || tc.GetArrayLength() == 0)
            return false;
        return true;
    }

    private static bool IsAssistantToolCallsMissingReasoning(JsonElement m)
    {
        if (!IsAssistantWithNonEmptyToolCalls(m))
            return false;
        if (!m.TryGetProperty("reasoning_content", out var rc))
            return true;
        if (rc.ValueKind == JsonValueKind.Null)
            return true;
        return rc.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(rc.GetString());
    }

    private static byte[] RewriteRootUnified(
        JsonElement root,
        IReadOnlyList<string> reasoningsByAssistantToolRound,
        MergedModelProfile? profile,
        out int injectedSlots,
        out int strippedEmptyTextParts)
    {
        injectedSlots = 0;
        strippedEmptyTextParts = 0;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (string.Equals(p.Name, "messages", StringComparison.Ordinal))
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    var assistantToolOrdinal = 0;
                    foreach (var msg in p.Value.EnumerateArray())
                    {
                        if (IsAssistantWithNonEmptyToolCalls(msg))
                        {
                            if (IsAssistantToolCallsMissingReasoning(msg)
                                && assistantToolOrdinal < reasoningsByAssistantToolRound.Count)
                            {
                                WriteAssistantMessageSanitized(
                                    writer,
                                    msg,
                                    reasoningsByAssistantToolRound[assistantToolOrdinal],
                                    ref strippedEmptyTextParts);
                                injectedSlots++;
                            }
                            else
                            {
                                WriteAssistantMessageSanitized(writer, msg, reasoningOverride: null, ref strippedEmptyTextParts);
                            }

                            assistantToolOrdinal++;
                        }
                        else if (IsAssistantRole(msg))
                        {
                            WriteAssistantMessageSanitized(writer, msg, reasoningOverride: null, ref strippedEmptyTextParts);
                        }
                        else
                        {
                            msg.WriteTo(writer);
                        }
                    }

                    writer.WriteEndArray();
                }
                else if (profile is { UseThinkingKeepAll: true }
                         && string.Equals(p.Name, "thinking", StringComparison.Ordinal))
                {
                    // 由下方统一写 thinking
                }
                else
                {
                    writer.WritePropertyName(p.Name);
                    p.Value.WriteTo(writer);
                }
            }

            if (profile is { UseThinkingKeepAll: true })
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "enabled");
                writer.WriteString("keep", "all");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>仅合并 <c>thinking.keep</c>，不改 messages。</summary>
    private static byte[]? MergeThinkingKeepOnly(JsonElement root, MergedModelProfile? profile)
    {
        if (profile is not { UseThinkingKeepAll: true })
            return null;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (string.Equals(p.Name, "thinking", StringComparison.Ordinal))
                    continue;
                writer.WritePropertyName(p.Name);
                p.Value.WriteTo(writer);
            }

            writer.WritePropertyName("thinking");
            writer.WriteStartObject();
            writer.WriteString("type", "enabled");
            writer.WriteString("keep", "all");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    private static void WriteAssistantMessageSanitized(
        Utf8JsonWriter writer,
        JsonElement msg,
        string? reasoningOverride,
        ref int strippedEmptyTextParts)
    {
        writer.WriteStartObject();
        foreach (var prop in msg.EnumerateObject())
        {
            if (string.Equals(prop.Name, "reasoning_content", StringComparison.Ordinal))
            {
                if (reasoningOverride != null)
                    continue;
                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
                continue;
            }

            if (string.Equals(prop.Name, "content", StringComparison.Ordinal))
            {
                writer.WritePropertyName("content");
                WriteSanitizedAssistantContent(writer, prop.Value, ref strippedEmptyTextParts);
                continue;
            }

            writer.WritePropertyName(prop.Name);
            prop.Value.WriteTo(writer);
        }

        if (reasoningOverride != null)
            writer.WriteString("reasoning_content", reasoningOverride);

        writer.WriteEndObject();
    }

    private static void WriteSanitizedAssistantContent(Utf8JsonWriter writer, JsonElement content, ref int strippedEmptyTextParts)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            case JsonValueKind.String:
                {
                    var s = content.GetString() ?? "";
                    if (s.Length == 0)
                    {
                        writer.WriteStringValue(EmptyContentPlaceholder);
                        strippedEmptyTextParts++;
                    }
                    else
                    {
                        writer.WriteStringValue(s);
                    }

                    return;
                }
            case JsonValueKind.Array:
                WriteSanitizedContentArray(writer, content, ref strippedEmptyTextParts);
                return;
            default:
                content.WriteTo(writer);
                return;
        }
    }

    private static void WriteSanitizedContentArray(Utf8JsonWriter writer, JsonElement contentArray, ref int strippedEmptyTextParts)
    {
        var kept = new List<JsonElement>();
        foreach (var part in contentArray.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                kept.Add(part);
                continue;
            }

            if (!part.TryGetProperty("type", out var tp) || tp.ValueKind != JsonValueKind.String)
            {
                kept.Add(part);
                continue;
            }

            if (!string.Equals(tp.GetString(), "text", StringComparison.Ordinal))
            {
                kept.Add(part);
                continue;
            }

            if (!part.TryGetProperty("text", out var tx))
            {
                strippedEmptyTextParts++;
                continue;
            }

            if (tx.ValueKind == JsonValueKind.Null)
            {
                strippedEmptyTextParts++;
                continue;
            }

            if (tx.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(tx.GetString()))
            {
                strippedEmptyTextParts++;
                continue;
            }

            kept.Add(part);
        }

        if (kept.Count == 0)
        {
            writer.WriteStringValue(EmptyContentPlaceholder);
            return;
        }

        writer.WriteStartArray();
        foreach (var el in kept)
            el.WriteTo(writer);
        writer.WriteEndArray();
    }
}
