using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;

namespace OpenWorkmate.Server.Services.Chat;

/// <summary>
/// 桥接 WebSocket HITL 响应 → MAF Workflow <see cref="StreamingRun"/> 的 <c>SendResponseAsync</c>。
/// <para>
/// 当前架构：HITL 仍通过 <see cref="HitlManager"/> 的 <c>TaskCompletionSource</c> 在中间件内阻塞等待。
/// 本类为未来迁移到 Workflow-level <c>RequestPort</c> 暂停/恢复模式预置基础设施。
/// </para>
/// <para>迁移路径：</para>
/// <list type="number">
/// <item>在 <c>ToolInvocationMiddleware</c> 中检测到需要 HITL 时，返回 <see cref="Microsoft.Extensions.AI.ToolApprovalRequestContent"/>。</item>
/// <item>MAF 的 <c>AIAgentHost</c>（<c>InterceptUserInputRequests = true</c>）将其转为 Workflow <c>RequestInfoEvent</c>。</item>
/// <item>本桥接类监听 <c>RequestInfoEvent</c> → 向前端发 <c>confirm_request</c> → 收到 <c>confirm_response</c> 后通过 <see cref="StreamingRun.SendResponseAsync"/> 恢复工作流。</item>
/// </list>
/// </summary>
public sealed class WorkflowHitlBridge
{
    private readonly ConcurrentDictionary<string, StreamingRun> _activeRuns = new(StringComparer.Ordinal);

    /// <summary>注册一个活跃的流式工作流运行实例，以便后续 HITL 响应可恢复它。</summary>
    public void RegisterRun(string sessionId, StreamingRun run) => _activeRuns[sessionId] = run;

    /// <summary>移除已完成的工作流运行。</summary>
    public void UnregisterRun(string sessionId) => _activeRuns.TryRemove(sessionId, out _);

    /// <summary>
    /// 发送 HITL 响应到对应的工作流运行（未来使用）。
    /// 当前返回 <c>false</c> 表示没有活跃的 workflow-level HITL 等待；调用方应回退到 <see cref="HitlManager.HandleResponse"/>。
    /// </summary>
    public async Task<bool> TryRespondAsync(string sessionId, string requestId, ExternalResponse response)
    {
        if (!_activeRuns.TryGetValue(sessionId, out var run))
            return false;
        await run.SendResponseAsync(response).ConfigureAwait(false);
        return true;
    }
}
