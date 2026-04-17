using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Logging;

namespace OfficeCopilot.Server.Services.Chat;

/// <summary>
/// 送入 MAF 主会话前：结构化 Debug 日志 + 可选落盘（仅当设置环境变量 <c>OFFICECOPILOT_CONTEXT_SNAPSHOT_DIR</c>）。
/// 不改变 <see cref="StreamChatTurnContext.HistoryToUse"/> 语义。
/// </summary>
public static class ContextTurnSnapshot
{
    /// <summary>
    /// 记录本轮 <paramref name="historyToUse"/> 轮廓；若配置了快照目录则写入 JSON（含消息预览，非全量正文）。
    /// </summary>
    public static void TryLogAndOptionalFile(
        string sessionId,
        string roundId,
        IReadOnlyList<ChatMessage> historyToUse,
        ILogger logger)
    {
        long totalChars = 0;
        var sysLen = 0;
        var seenFirstSystem = false;
        foreach (var m in historyToUse)
        {
            var t = m.Text?.Length ?? 0;
            totalChars += t;
            if (m.Role == ChatRole.System && !seenFirstSystem)
            {
                seenFirstSystem = true;
                sysLen = t;
            }
        }

        logger.LogDebug(
            "[{SessionId}] [{RoundId}] ContextSnapshot: messages={Count}, firstSystemChars={SysChars}, totalTextChars={Total}",
            sessionId, roundId, historyToUse.Count, sysLen, totalChars);

        var dir = Environment.GetEnvironmentVariable("OFFICECOPILOT_CONTEXT_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;

        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir.Trim(), $"{roundId}.json");
            var messages = new List<object>(historyToUse.Count);
            for (var i = 0; i < historyToUse.Count; i++)
            {
                var m = historyToUse[i];
                var text = m.Text ?? "";
                messages.Add(new
                {
                    index = i,
                    role = m.Role.ToString(),
                    textLength = text.Length,
                    preview = LogPreview.HeadTail(text, 240, 240)
                });
            }

            var payload = new { sessionId, roundId, capturedAtUtc = DateTimeOffset.UtcNow, messages };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, Utf8JsonFileOptions.Indented));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{SessionId}] [{RoundId}] Context snapshot file write failed.", sessionId, roundId);
        }
    }
}
