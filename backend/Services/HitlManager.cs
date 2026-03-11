using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// Manages Human-In-The-Loop confirmation: sends confirm_request to the frontend
/// and waits for confirm_response (or timeout) before continuing or aborting.
/// </summary>
public sealed class HitlManager
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private readonly SessionManager _sessionManager;
    private readonly ILogger<HitlManager> _logger;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public HitlManager(SessionManager sessionManager, ILogger<HitlManager> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Sends a confirm_request to the session's client and returns a task that completes
    /// with true (allowed) or false (denied or timeout).
    /// </summary>
    public async Task<bool> RequestConfirmationAsync(string sessionId, string action, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.TryAdd(requestId, tcs);

        var msg = new
        {
            type = "confirm_request",
            id = requestId,
            content = action
        };
        var json = JsonSerializer.Serialize(msg);
        await _sessionManager.SendToAsync(sessionId, json);

        _logger.LogInformation("[Hitl] confirm_request id={ReqId} sessionId={SessionId} action={Action}", requestId, sessionId, action);

        var delayTask = Task.Delay(DefaultTimeout, ct);
        var completed = await Task.WhenAny(tcs.Task, delayTask);
        if (completed == tcs.Task)
            return tcs.Task.Result;
        _logger.LogWarning("[Hitl] confirm_request id={ReqId} timed out", requestId);
        _pending.TryRemove(requestId, out var expired);
        expired?.TrySetResult(false);
        return false;
    }

    /// <summary>
    /// Called when the frontend sends confirm_response. Completes the pending request.
    /// </summary>
    public void HandleResponse(string requestId, bool allowed)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            _logger.LogInformation("[Hitl] confirm_response id={ReqId} allowed={Allowed}", requestId, allowed);
            tcs.TrySetResult(allowed);
        }
        else
        {
            _logger.LogWarning("[Hitl] confirm_response for unknown id={ReqId}", requestId);
        }
    }
}
