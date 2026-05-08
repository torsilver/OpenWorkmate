using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWorkmate.Server.Services;

public class RpcManager
{
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<JsonElement?> Tcs, CancellationTokenSource Cts)> _pendingRequests = new();
    private readonly ILogger<RpcManager> _logger;

    public RpcManager(ILogger<RpcManager> logger) => _logger = logger;

    public string RegisterRequest(out Task<JsonElement?> task, CancellationToken externalCt = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement?>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _pendingRequests.TryAdd(id, (tcs, cts));
        task = tcs.Task;
        _logger.LogDebug("[RPC] Register request id={ReqId} pending={Count}", id, _pendingRequests.Count);

        _ = Task.Delay(TimeSpan.FromSeconds(60), cts.Token).ContinueWith(_ =>
        {
            if (_pendingRequests.TryRemove(id, out var entry))
            {
                _logger.LogWarning("[RPC] Request id={ReqId} timed out", id);
                entry.Tcs.TrySetException(new TimeoutException("RPC 请求超时（60秒），请稍后重试。"));
                entry.Cts.Dispose();
            }
        }, TaskScheduler.Default);

        return id;
    }

    public void HandleResponse(string id, JsonElement? result, JsonElement? error)
    {
        if (_pendingRequests.TryRemove(id, out var entry))
        {
            _logger.LogDebug("[RPC] Response id={ReqId} hasError={HasError}", id, error != null);
            entry.Cts.Dispose();
            if (error != null)
            {
                entry.Tcs.TrySetException(new Exception($"Frontend RPC Error: {error}"));
            }
            else
            {
                entry.Tcs.TrySetResult(result);
            }
        }
        else
        {
            _logger.LogWarning("[RPC] Response for unknown id={ReqId}", id);
        }
    }
}
