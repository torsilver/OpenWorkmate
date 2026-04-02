using System.Text.Json;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>
/// 百炼 OpenAI 兼容 <c>POST .../chat/completions</c> 请求体合并（非标准顶层字段）。
/// </summary>
/// <remarks>
/// 文档：<see href="https://help.aliyun.com/zh/model-studio/qwq"/>（思考、reasoning_content）、
/// <see href="https://help.aliyun.com/zh/model-studio/developer-reference/compatibility-of-openai-with-dashscope"/>（enable_search、stream_options 等）。
/// 历史上兼容页曾写 tools 与 stream 不可同用，请以当前模型实测为准（本产品在主对话中依赖流式 + 工具）。
/// </remarks>
public static class DashScopeChatRequestMerge
{
    /// <summary>是否应对该 URI 做百炼请求/响应处理（compatible-mode 对话）。</summary>
    public static bool IsDashScopeChatCompletions(Uri? requestUri)
    {
        if (requestUri == null || !requestUri.IsAbsoluteUri)
            return false;
        if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requestUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        var host = requestUri.Host;
        if (!host.Contains("dashscope", StringComparison.OrdinalIgnoreCase))
            return false;
        return requestUri.AbsolutePath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 将百炼相关字段合并进已有 JSON 请求体。返回 null 表示无需替换原文。
    /// 后台调用且 <see cref="AiModelEntry.DisableThinkingForBackgroundCalls"/> 为 true 时强制 <c>enable_thinking: false</c>。
    /// </summary>
    public static byte[]? MergeChatCompletionUtf8Body(ReadOnlySpan<byte> bodyUtf8, AiModelEntry? entry)
    {
        if (bodyUtf8.IsEmpty)
            return null;

        using var doc = JsonDocument.Parse(bodyUtf8.ToArray());
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var bg = DashScopeCallKindContext.IsBackground;
        var forceNoThink = bg && entry?.DisableThinkingForBackgroundCalls == true;

        var skip = new HashSet<string>(StringComparer.Ordinal);
        var willWrite = false;

        if (forceNoThink || entry?.EnableThinking is not null)
        {
            skip.Add("enable_thinking");
            willWrite = true;
        }

        if (entry?.ThinkingBudget is int tb && tb > 0)
        {
            skip.Add("thinking_budget");
            willWrite = true;
        }

        if (entry?.EnableSearch is not null)
        {
            skip.Add("enable_search");
            willWrite = true;
        }

        if (entry is { SearchOptionsJson: { } soj } && !string.IsNullOrWhiteSpace(soj))
        {
            try
            {
                using var _ = JsonDocument.Parse(soj);
                skip.Add("search_options");
                willWrite = true;
            }
            catch (JsonException)
            {
                /* invalid */
            }
        }

        // 百炼「混合思考」文档示例中与 enable_thinking 同时传 stream_options.include_usage；Cherry Studio 等 OpenAI 客户端流式也常带此项。
        // 未开启思考或未强制关思考的主对话：仅按 StreamIncludeUsage 写入。
        var writeStreamOptions = entry?.StreamIncludeUsage == true
            || (entry?.EnableThinking == true && !forceNoThink);

        if (writeStreamOptions)
        {
            skip.Add("stream_options");
            willWrite = true;
        }

        var root = doc.RootElement;
        var normalizeToolChoice = ShouldNormalizeToolChoiceForThinking(root, entry, forceNoThink);

        if (!willWrite && !normalizeToolChoice)
            return null;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (willWrite && skip.Contains(prop.Name))
                    continue;
                if (normalizeToolChoice
                    && string.Equals(prop.Name, "tool_choice", StringComparison.OrdinalIgnoreCase))
                    continue;
                prop.WriteTo(writer);
            }

            if (forceNoThink)
                writer.WriteBoolean("enable_thinking", false);
            else if (entry?.EnableThinking is { } et)
                writer.WriteBoolean("enable_thinking", et);

            if (entry?.ThinkingBudget is { } tb2 && tb2 > 0)
                writer.WriteNumber("thinking_budget", tb2);

            if (entry?.EnableSearch is { } es)
                writer.WriteBoolean("enable_search", es);

            if (entry is { SearchOptionsJson: { } soj2 } && !string.IsNullOrWhiteSpace(soj2))
            {
                try
                {
                    using var soDoc = JsonDocument.Parse(soj2);
                    if (soDoc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("search_options");
                        soDoc.RootElement.WriteTo(writer);
                    }
                }
                catch (JsonException)
                {
                    /* skip */
                }
            }

            if (writeStreamOptions)
            {
                writer.WritePropertyName("stream_options");
                writer.WriteStartObject();
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            if (normalizeToolChoice)
                writer.WriteString("tool_choice", "auto");

            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>
    /// 百炼在开启思考模式时不接受 <c>tool_choice</c> 为 <c>required</c> 或 object；与 SK 的 Required 工具策略冲突时需降级为 <c>auto</c>。
    /// </summary>
    internal static bool EffectiveEnableThinkingOn(JsonElement root, AiModelEntry? entry, bool forceNoThink)
    {
        if (forceNoThink)
            return false;
        if (entry?.EnableThinking == true)
            return true;
        return root.TryGetProperty("enable_thinking", out var et)
               && et.ValueKind == JsonValueKind.True;
    }

    internal static bool IsToolChoiceIncompatibleWithThinking(JsonElement root)
    {
        if (!root.TryGetProperty("tool_choice", out var tc))
            return false;
        if (tc.ValueKind == JsonValueKind.String)
            return string.Equals(tc.GetString(), "required", StringComparison.OrdinalIgnoreCase);
        return tc.ValueKind == JsonValueKind.Object;
    }

    internal static bool ShouldNormalizeToolChoiceForThinking(JsonElement root, AiModelEntry? entry, bool forceNoThink) =>
        EffectiveEnableThinkingOn(root, entry, forceNoThink) && IsToolChoiceIncompatibleWithThinking(root);

    /// <summary>请求是否为流式（用于决定是否包装响应 SSE 旁路）。</summary>
    public static bool RequestBodyIndicatesStream(ReadOnlySpan<byte> bodyUtf8)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyUtf8.ToArray());
            return doc.RootElement.TryGetProperty("stream", out var s)
                   && s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
