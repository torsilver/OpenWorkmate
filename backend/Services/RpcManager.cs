using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server.Services;

public class RpcManager
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    private readonly ILogger<RpcManager> _logger;

    public RpcManager(ILogger<RpcManager> logger) => _logger = logger;

    public string RegisterRequest(out Task<JsonElement?> task)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pendingRequests.TryAdd(id, tcs);
        task = tcs.Task;
        _logger.LogDebug("[RPC] Register request id={ReqId} pending={Count}", id, _pendingRequests.Count);

        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
        {
            if (_pendingRequests.TryRemove(id, out var expiredTcs))
            {
                _logger.LogWarning("[RPC] Request id={ReqId} timed out", id);
                expiredTcs.TrySetException(new TimeoutException("RPC 请求超时（60秒），请稍后重试。"));
            }
        });

        return id;
    }

    public void HandleResponse(string id, JsonElement? result, JsonElement? error)
    {
        if (_pendingRequests.TryRemove(id, out var tcs))
        {
            _logger.LogDebug("[RPC] Response id={ReqId} hasError={HasError}", id, error != null);
            if (error != null)
            {
                tcs.TrySetException(new Exception($"Frontend RPC Error: {error}"));
            }
            else
            {
                tcs.TrySetResult(result);
            }
        }
        else
        {
            _logger.LogWarning("[RPC] Response for unknown id={ReqId}", id);
        }
    }
}
