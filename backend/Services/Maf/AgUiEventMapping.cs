namespace OfficeCopilot.Server.Services.Maf;

/// <summary>
/// AG-UI 事件映射：当前自定义 WebSocket 消息类型 → AG-UI SSE 事件类型的对照表。
/// <para>
/// 后端双轨期间（WebSocket + AG-UI 并存），此映射用于指导逐步迁移。
/// 前端按 AG-UI 标准事件接收后，旧 WebSocket 路径可逐步下线。
/// </para>
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>当前 WS 类型</term><description>AG-UI 等价事件</description></listheader>
/// <item><term><c>stream_start</c></term><description><c>RunStarted</c></description></item>
/// <item><term><c>stream_end</c></term><description><c>RunFinished</c></description></item>
/// <item><term><c>stream_chunk</c></term><description><c>TextMessageContent</c> (SSE text delta)</description></item>
/// <item><term><c>reasoning_chunk</c></term><description>AG-UI <c>CustomEvent</c> (无直接对应)</description></item>
/// <item><term><c>tool_call_delta</c></term><description><c>ToolCallStart</c> / <c>ToolCallArgs</c></description></item>
/// <item><term><c>tool_invocation_start</c></term><description><c>ToolCallStart</c></description></item>
/// <item><term><c>tool_invocation_end</c></term><description><c>ToolCallEnd</c></description></item>
/// <item><term><c>confirm_request</c></term><description>AG-UI Frontend Tool Rendering (<c>StateSnapshot</c> + 前端工具)</description></item>
/// <item><term><c>ask_options_request</c></term><description>AG-UI Frontend Tool Rendering (<c>StateSnapshot</c> + 前端工具)</description></item>
/// <item><term><c>agent_status</c></term><description><c>StepStarted</c></description></item>
/// <item><term><c>agent_trace</c></term><description><c>StepStarted</c> + metadata</description></item>
/// <item><term><c>agent_phase</c></term><description><c>StepStarted</c> / <c>StepFinished</c></description></item>
/// <item><term><c>subtask_*</c></term><description>嵌套 Run 事件 (需确认 AG-UI 支持)</description></item>
/// </list>
/// </remarks>
public static class AgUiEventMapping
{
    /// <summary>当前 AG-UI 双轨模式说明（可通过 <c>/api/agui-status</c> 暴露给前端）。</summary>
    public static object GetStatus() => new
    {
        aguiEndpoint = "/agui",
        wsEndpoint = "/ws",
        mode = "dual-track",
        note = "AG-UI SSE endpoint is experimental; WebSocket remains the primary protocol for all three frontends."
    };
}
