namespace OpenWorkmate.Server;

/// <summary>模型流式输出的单次工具调用增量（供 WebSocket tool_call_delta 下发）。</summary>
public sealed record ToolCallStreamDelta(string CallId, string? ToolName, string ArgumentsDelta);
