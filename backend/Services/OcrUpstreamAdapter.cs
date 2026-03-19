using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// OCR 上游适配器（OpenAI-Compatible 多模态识图）：
/// - URL 规范化：endpoint -> endpoint/chat/completions
/// - 请求体构建：{ model, messages[{role,user, content:[{type:image_url},{type:text}] }]}
/// - 响应解析：choices[0].message.content
/// </summary>
public static class OcrUpstreamAdapter
{
    public static string BuildDashScopeChatCompletionsUrl(string endpoint)
    {
        var ep = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(ep))
            throw new InvalidOperationException("OCR endpoint 无效。");

        if (ep.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return ep;

        return ep + "/chat/completions";
    }

    public static string BuildDataUrlFromImageBytes(byte[] imageBytes, string? contentType)
    {
        var ct = string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType.Trim();
        var base64 = Convert.ToBase64String(imageBytes ?? Array.Empty<byte>());
        return "data:" + ct + ";base64," + base64;
    }

    public static string BuildDashScopeOpenAICompatibleOcrRequestJson(
        string modelId,
        string dataUrl,
        string prompt,
        string? languageHint)
    {
        var finalModelId = (modelId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(finalModelId))
            throw new InvalidOperationException("OCR modelId 无效。");

        var finalPrompt = prompt ?? "";

        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            var lh = languageHint.Trim();
            // 如果 languageHint 本身就是 qwen-*，把它当作模型覆盖（满足你对齐百炼模型的需求）
            if (lh.StartsWith("qwen-", StringComparison.OrdinalIgnoreCase))
            {
                finalModelId = lh;
            }
            else if (!string.IsNullOrWhiteSpace(lh))
            {
                // 否则当作识别语言提示拼进 prompt
                finalPrompt += "\n识别语言提示：" + lh;
            }
        }

        if (string.IsNullOrWhiteSpace(dataUrl))
            throw new InvalidOperationException("OCR dataUrl 无效。");

        // OpenAI-compatible: chat.completions
        var payload = new Dictionary<string, object?>
        {
            ["model"] = finalModelId,
            ["temperature"] = 0,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image_url",
                            image_url = new { url = dataUrl }
                        },
                        new
                        {
                            type = "text",
                            text = finalPrompt
                        }
                    }
                }
            }
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(payload, options);
    }

    public static string ExtractOcrTextFromChatCompletionsResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind == JsonValueKind.String)
                {
                    return contentEl.GetString() ?? string.Empty;
                }
            }

            // 兜底：部分实现可能直接返回 text
            if (root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString() ?? string.Empty;
        }
        catch
        {
            // 忽略解析失败，直接把原文返回更方便排查
        }

        return responseText;
    }
}

