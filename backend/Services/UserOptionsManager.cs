using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server.Services;

public sealed record AskOptionsStep(string StepId, string Question, List<AskOptionsOption> Options);

public sealed record AskOptionsOption(string OptionId, string Label);

/// <summary>Outcome of <see cref="UserOptionsManager.RequestOptionsAsync"/>.</summary>
public sealed record AskOptionsRequestResult(bool TimedOut, IReadOnlyDictionary<string, string> Selections);

/// <summary>
/// Manages multi-round "AI candidate options" confirmations:
/// backend sends `ask_options_request`, waits for `ask_options_response` with selections.
/// </summary>
public sealed class UserOptionsManager
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, string>>> _pending = new();
    private readonly Func<string, string, Task> _sendToSession;
    private readonly ILogger<UserOptionsManager> _logger;
    private readonly TimeSpan _requestTimeout;

    /// <summary>Default wait for <see cref="RequestOptionsAsync"/> (seconds); user-facing error text should stay in sync.</summary>
    public const int AskOptionsWaitSeconds = 90;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(AskOptionsWaitSeconds);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UserOptionsManager(SessionManager sessionManager, ILogger<UserOptionsManager> logger)
        : this((sessionId, message) => sessionManager.SendToAsync(sessionId, message), logger, null)
    {
    }

    /// <summary>Tests: custom send callback and optional timeout override.</summary>
    internal UserOptionsManager(
        Func<string, string, Task> sendToSession,
        ILogger<UserOptionsManager> logger,
        TimeSpan? requestTimeout = null)
    {
        _sendToSession = sendToSession ?? throw new ArgumentNullException(nameof(sendToSession));
        _logger = logger;
        _requestTimeout = requestTimeout ?? DefaultTimeout;
    }

    public async Task<AskOptionsRequestResult> RequestOptionsAsync(
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
        await _sendToSession(sessionId, json).ConfigureAwait(false);

        _logger.LogInformation("[UserOptions] ask_options_request id={ReqId} steps={StepCount} sessionId={SessionId}", requestId, steps?.Count ?? 0, sessionId);

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delayCts.CancelAfter(_requestTimeout);
        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, delayCts.Token);
        var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
        if (completed == tcs.Task)
        {
            var dict = await tcs.Task.ConfigureAwait(false);
            return new AskOptionsRequestResult(false, dict);
        }

        try
        {
            await delayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                if (_pending.TryRemove(requestId, out var orphan))
                    orphan.TrySetCanceled(ct);
                throw;
            }
        }

        _logger.LogWarning("[UserOptions] ask_options_request id={ReqId} timed out", requestId);
        if (_pending.TryRemove(requestId, out var left))
            left.TrySetCanceled(CancellationToken.None);

        return new AskOptionsRequestResult(true, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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
