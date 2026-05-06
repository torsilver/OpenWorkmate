using System.Text.Json;

namespace OfficeCopilot.Server.Services.OpenAiCompat;

internal static class OpenAiReasoningEchoSseHelpers
{
    /// <summary>判断 OpenAI 兼容 SSE <c>data:</c> 行是否表明本轮 assistant 将产生 tool_calls（需在流结束后把 reasoning 入队供下轮 HTTP patch）。</summary>
    internal static bool JsonLineIndicatesToolCalls(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
                return false;
            var c0 = choices[0];
            if (c0.TryGetProperty("finish_reason", out var fr)
                && fr.ValueKind == JsonValueKind.String
                && string.Equals(fr.GetString(), "tool_calls", StringComparison.Ordinal))
                return true;
            if (c0.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("tool_calls", out var tc)
                && tc.ValueKind == JsonValueKind.Array
                && tc.GetArrayLength() > 0)
                return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
