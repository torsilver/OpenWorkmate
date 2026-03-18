using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Plugins;

namespace OfficeCopilot.Server.Services;

public sealed record AskOptionsStep(string StepId, string Question, List<AskOptionsOption> Options);

public sealed record AskOptionsOption(string OptionId, string Label);

/// <summary>
/// Manages multi-round "AI candidate options" confirmations:
/// backend sends `ask_options_request`, waits for `ask_options_response` with selections.
/// </summary>
public sealed class UserOptionsManager
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, string>>> _pending = new();
    private readonly SessionManager _sessionManager;
    private readonly ILogger<UserOptionsManager> _logger;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UserOptionsManager(SessionManager sessionManager, ILogger<UserOptionsManager> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> RequestOptionsAsync(
        string sessionId,
        string title,
        string prompt,
        IReadOnlyList<AskOptionsStep> steps,
        CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.TryAdd(requestId, tcs);

        var msg = new
        {
            type = "ask_options_request",
            id = requestId,
            title = title ?? "",
            prompt = prompt ?? "",
            steps = steps.Select(s => new
            {
                stepId = s.StepId,
                question = s.Question,
                options = (s.Options ?? new List<AskOptionsOption>())
                    .Select(o => new { optionId = o.OptionId, label = o.Label })
                    .ToList()
            }).ToList()
        };

        var json = JsonSerializer.Serialize(msg, JsonOptions);
        await _sessionManager.SendToAsync(sessionId, json);

        _logger.LogInformation("[UserOptions] ask_options_request id={ReqId} steps={StepCount} sessionId={SessionId}", requestId, steps?.Count ?? 0, sessionId);

        var delayTask = Task.Delay(DefaultTimeout, ct);
        var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
        if (completed == tcs.Task)
            return tcs.Task.Result;

        _logger.LogWarning("[UserOptions] ask_options_request id={ReqId} timed out", requestId);
        _pending.TryRemove(requestId, out _);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void HandleResponse(string requestId, Dictionary<string, string>? selections)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        if (!_pending.TryRemove(requestId, out var tcs))
        {
            _logger.LogWarning("[UserOptions] ask_options_response for unknown id={ReqId}", requestId);
            return;
        }

        var safe = selections ?? new Dictionary<string, string>();
        tcs.TrySetResult(new Dictionary<string, string>(safe, StringComparer.OrdinalIgnoreCase));
        _logger.LogInformation("[UserOptions] ask_options_response id={ReqId} selectionsCount={Count}", requestId, safe.Count);
    }
}

