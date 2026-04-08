using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

public sealed record HitlResult(bool Allowed, bool AddToAllowList);

/// <summary>
/// Manages Human-In-The-Loop confirmation: sends confirm_request to the frontend
/// and waits for confirm_response (or timeout) before continuing or aborting.
/// </summary>
public sealed class HitlManager
{
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<HitlResult> Tcs, string? EndKey, string? Kind, string? AddKey)> _pending = new();
    private readonly SessionManager _sessionManager;
    private readonly ConfigService _configService;
    private readonly ILogger<HitlManager> _logger;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public HitlManager(SessionManager sessionManager, ConfigService configService, ILogger<HitlManager> logger)
    {
        _sessionManager = sessionManager;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Sends a confirm_request to the session's client and returns a task that completes
    /// with (allowed, addToAllowList). When hitlKind and addToAllowListKey are set, frontend may show "Add to AllowList" button.
    /// </summary>
    public async Task<HitlResult> RequestConfirmationAsync(
        string sessionId,
        string action,
        string? hitlKind = null,
        string? addToAllowListKey = null,
        string? humanSummary = null,
        CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<HitlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientType = _sessionManager.GetClientType(sessionId);
        var endKey = CliScriptEndKeys.ResolveEndKey(clientType);
        _pending.TryAdd(requestId, (tcs, endKey, hitlKind, addToAllowListKey));

        var msg = new Dictionary<string, object?>
        {
            ["type"] = "confirm_request",
            ["id"] = requestId,
            ["content"] = action,
            ["hitlTimeoutSeconds"] = (int)DefaultTimeout.TotalSeconds
        };
        if (!string.IsNullOrEmpty(hitlKind))
            msg["hitlKind"] = hitlKind;
        if (!string.IsNullOrEmpty(addToAllowListKey))
            msg["addToAllowListKey"] = addToAllowListKey;
        if (!string.IsNullOrWhiteSpace(humanSummary))
            msg["humanSummary"] = humanSummary.Trim();

        var json = JsonSerializer.Serialize(msg, JsonOptions);
        await _sessionManager.SendToAsync(sessionId, json);

        _logger.LogInformation("[Hitl] confirm_request id={ReqId} sessionId={SessionId} action={Action} hitlKind={Kind}", requestId, sessionId, action, hitlKind);

        var delayTask = Task.Delay(DefaultTimeout, ct);
        var completed = await Task.WhenAny(tcs.Task, delayTask);
        if (completed == tcs.Task)
            return await tcs.Task.ConfigureAwait(false);
        _logger.LogWarning("[Hitl] confirm_request id={ReqId} timed out", requestId);
        _pending.TryRemove(requestId, out var expired);
        expired.Tcs?.TrySetResult(new HitlResult(false, false));
        return new HitlResult(false, false);
    }

    /// <summary>
    /// Called when the frontend sends confirm_response. Completes the pending request; when addToAllowList is true, adds the key to the end's whitelist and persists.
    /// </summary>
    public void HandleResponse(string requestId, bool allowed, bool addToAllowList = false)
    {
        if (!_pending.TryRemove(requestId, out var pending))
        {
            _logger.LogWarning("[Hitl] confirm_response for unknown id={ReqId}", requestId);
            return;
        }

        _logger.LogInformation("[Hitl] confirm_response id={ReqId} allowed={Allowed} addToAllowList={Add}", requestId, allowed, addToAllowList);

        if (addToAllowList && allowed && !string.IsNullOrEmpty(pending.EndKey) && !string.IsNullOrEmpty(pending.Kind) && !string.IsNullOrEmpty(pending.AddKey))
        {
            if (string.Equals(pending.Kind, "run_command", StringComparison.OrdinalIgnoreCase))
                _configService.AddAllowedCliCommandForEnd(pending.EndKey, pending.AddKey);
            else if (string.Equals(pending.Kind, "run_page_script", StringComparison.OrdinalIgnoreCase))
                _configService.AddAllowedPageScriptIdForEnd(pending.EndKey, pending.AddKey);
        }

        pending.Tcs.TrySetResult(new HitlResult(allowed, addToAllowList));
    }
}
