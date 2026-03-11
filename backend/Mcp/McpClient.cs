using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace OfficeCopilot.Server.Mcp;

public class McpClient : IDisposable
{
    private readonly string _id;
    private readonly string _command;
    private readonly string[] _args;
    private readonly ILogger _logger;
    private Process? _process;
    private StreamReader? _stdout;
    private StreamWriter? _stdin;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pendingRequests = new();
    private CancellationTokenSource? _readCts;

    public string Id => _id;

    public McpClient(string id, string command, string[] args, ILogger logger)
    {
        _id = id;
        _command = command;
        _args = args;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in _args)
        {
            psi.ArgumentList.Add(arg);
        }

        _process = new Process { StartInfo = psi };
        
        _process.ErrorDataReceived += (s, e) => 
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogWarning("[MCP {Id} STDERR] {Data}", _id, e.Data);
            }
        };

        if (!_process.Start())
        {
            throw new Exception($"Failed to start MCP Server: {_command}");
        }

        _process.BeginErrorReadLine();

        _stdout = _process.StandardOutput;
        _stdin = _process.StandardInput;

        _readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token), CancellationToken.None);

        // 初始化握手
        await InitializeAsync(ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stdout != null && !_stdout.EndOfStream)
            {
                var line = await _stdout.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var response = JsonSerializer.Deserialize<JsonRpcResponse>(line);
                    if (response != null && response.Id != null && _pendingRequests.TryGetValue(response.Id, out var tcs))
                    {
                        tcs.SetResult(response);
                        _pendingRequests.TryRemove(response.Id, out _);
                    }
                    else
                    {
                        // 可能是服务器发来的通知 (Notification) 或不支持的请求，忽略
                        _logger.LogDebug("[MCP {Id}] Received unhandled message: {Line}", _id, line);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MCP {Id}] Failed to parse incoming JSON-RPC: {Line}", _id, line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MCP {Id}] ReadLoop crashed.", _id);
        }
    }

    private async Task<JsonElement?> SendRequestAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = method,
            Params = parameters
        };

        var tcs = new TaskCompletionSource<JsonRpcResponse>();
        _pendingRequests[requestId] = tcs;

        var json = JsonSerializer.Serialize(request);
        await _stdin!.WriteLineAsync(json);
        await _stdin.FlushAsync();

        // 默认 30 秒超时
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        timeoutCts.Token.Register(() => tcs.TrySetCanceled());

        var response = await tcs.Task;

        if (response.Error != null)
        {
            throw new Exception($"MCP Error {response.Error.Code}: {response.Error.Message}");
        }

        return response.Result;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var initParams = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "OfficeCopilot", version = "1.0.0" }
        };

        await SendRequestAsync("initialize", initParams, ct);
        
        // 发送 initialized 通知
        var notif = new JsonRpcRequest { Method = "notifications/initialized" };
        await _stdin!.WriteLineAsync(JsonSerializer.Serialize(notif));
        await _stdin.FlushAsync();
        
        _logger.LogInformation("[MCP {Id}] Handshake complete.", _id);
    }

    public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var result = await SendRequestAsync("tools/list", null, ct);
        if (result.HasValue && result.Value.TryGetProperty("tools", out var toolsArray))
        {
            return toolsArray.Deserialize<List<McpTool>>() ?? new List<McpTool>();
        }
        return new List<McpTool>();
    }

    public async Task<McpCallToolResult> CallToolAsync(string name, Dictionary<string, object> args, CancellationToken ct = default)
    {
        var result = await SendRequestAsync("tools/call", new
        {
            name = name,
            arguments = args
        }, ct);

        if (result.HasValue)
        {
            return result.Value.Deserialize<McpCallToolResult>() ?? new McpCallToolResult();
        }
        
        throw new Exception("Tool call returned no result.");
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        _process?.Kill();
        _process?.Dispose();
        _stdout?.Dispose();
        _stdin?.Dispose();
    }
}
