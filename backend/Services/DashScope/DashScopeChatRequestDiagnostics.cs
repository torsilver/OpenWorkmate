using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services.DashScope;

/// <summary>百炼 chat/completions 出站 JSON 关键字段快照（仅用于日志）。</summary>
internal static class DashScopeChatRequestDiagnostics
{
    /// <summary>日志预览：前若干 + 后若干字符，中间省略（大 JSON 只看头尾即可对齐参数/收尾）。</summary>
    internal const int LogPreviewHeadChars = 300;
    internal const int LogPreviewTailChars = 300;

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    internal static void LogOutgoingBody(
        ILogger? logger,
        string modelEntryId,
        bool isBackground,
        AiModelEntry? entry,
        ReadOnlySpan<byte> bodyUtf8,
        bool mergedReplacedBody)
    {
        logger ??= NullLogger.Instance;
        if (bodyUtf8.IsEmpty)
        {
            logger.LogInformation(
                "[DashScope] req entry={Entry} bg={Bg} merged={Merged} body=empty",
                modelEntryId, isBackground, mergedReplacedBody);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(bodyUtf8.ToArray());
            var root = doc.RootElement;
            var stream = root.TryGetProperty("stream", out var st) && st.ValueKind == JsonValueKind.True;
            string? enableThinking = null;
            if (root.TryGetProperty("enable_thinking", out var et))
            {
                enableThinking = et.ValueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => et.ToString()
                };
            }
            else
                enableThinking = "(absent)";

            var hasStreamOptions = root.TryGetProperty("stream_options", out _);
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var toolsLen = root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array
                ? tools.GetArrayLength()
                : (int?)null;

            logger.LogInformation(
                "[DashScope] req entry={Entry} bg={Bg} merged={Merged} stream={Stream} enable_thinking={EnableThinking} has_stream_options={HasSo} model={Model} toolsCount={Tools} cfgEnableThinking={CfgEt}",
                modelEntryId,
                isBackground,
                mergedReplacedBody,
                stream,
                enableThinking,
                hasStreamOptions,
                model,
                toolsLen,
                entry?.EnableThinking?.ToString() ?? "(null)");

            var preview = BuildOutgoingBodyPreview(root);
            logger.LogInformation(
                "[DashScope] req entry={Entry} bodyUtf8Bytes={Bytes} bodyPreview={Preview}",
                modelEntryId,
                bodyUtf8.Length,
                preview);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[DashScope] req entry={Entry} outgoing body JSON parse failed len={Len}", modelEntryId, bodyUtf8.Length);
            var raw = Encoding.UTF8.GetString(bodyUtf8);
            logger.LogInformation(
                "[DashScope] req entry={Entry} bodyPreview(raw)={Preview}",
                modelEntryId,
                HeadTailOmitMiddle(raw, LogPreviewHeadChars, LogPreviewTailChars));
        }
    }

    /// <summary>前 <paramref name="headChars"/> + 后 <paramref name="tailChars"/>；中间用省略标记（单行日志友好）。</summary>
    internal static string HeadTailOmitMiddle(string s, int headChars = LogPreviewHeadChars, int tailChars = LogPreviewTailChars)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.Length <= headChars + tailChars)
            return s;
        var omitted = s.Length - headChars - tailChars;
        return string.Concat(
            s.Substring(0, headChars),
            " …[omitted ",
            omitted.ToString(),
            " chars]… ",
            s.Substring(s.Length - tailChars));
    }

    /// <summary>
    /// 将已解析的请求根节点序列化为日志用单行 JSON（中文等不转义为 <c>\uXXXX</c>），再按头尾省略。
    /// 大 body 也走同一路径：与「仅截取原始字符串」相比多一次 Serialize，但日志可读性更好。
    /// </summary>
    private static string BuildOutgoingBodyPreview(JsonElement root)
    {
        var readable = JsonSerializer.Serialize(root, LogJsonOptions);
        if (readable.Length <= LogPreviewHeadChars + LogPreviewTailChars)
            return readable;
        return HeadTailOmitMiddle(readable, LogPreviewHeadChars, LogPreviewTailChars);
    }

    /// <summary>SSE <c>data:</c> 单行 JSON 在日志中转为可读（去掉 <c>\uXXXX</c> 中文转义）。解析失败则返回原文。</summary>
    internal static string FormatSseJsonPayloadForLog(string jsonOneLine)
    {
        if (string.IsNullOrEmpty(jsonOneLine))
            return jsonOneLine;
        try
        {
            using var doc = JsonDocument.Parse(jsonOneLine);
            return JsonSerializer.Serialize(doc.RootElement, LogJsonOptions);
        }
        catch (JsonException)
        {
            return jsonOneLine;
        }
    }
}
