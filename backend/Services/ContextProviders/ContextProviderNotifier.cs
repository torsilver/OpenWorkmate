using System.Text.Json;

namespace OfficeCopilot.Server.Services.ContextProviders;

/// <summary>Context Provider 用的通知辅助：将 agent_status / agent_trace 推送到当前 WebSocket 会话。</summary>
internal static class ContextProviderNotifier
{
    public static async Task StatusAsync(SessionManager sm, string sessionId, string text, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(sessionId)) return;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return;
        if (t.Length > 200) t = t[..200];
        var msg = new WsMessage { Type = "agent_status", Content = t };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await sm.SendToAsync(sessionId, json).ConfigureAwait(false);
    }

    public static async Task TraceAsync(SessionManager sm, string sessionId, string category, string title, string? detail, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(sessionId)) return;
        var cat = (category ?? "").Trim();
        if (cat.Length == 0) return;
        var trTitle = AgentTraceFormatter.TruncateTitle(title);
        if (trTitle.Length == 0) return;
        var trDetail = AgentTraceFormatter.TruncateDetail(detail);
        var msg = new WsMessage
        {
            Type = "agent_trace",
            Content = trTitle,
            TraceCategory = cat,
            TraceTitle = trTitle,
            TraceDetail = string.IsNullOrEmpty(trDetail) ? null : trDetail
        };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        await sm.SendToAsync(sessionId, json).ConfigureAwait(false);
    }
}
