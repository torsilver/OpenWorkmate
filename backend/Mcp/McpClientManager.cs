using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OfficeCopilot.Server.Mcp;

public sealed class McpClientManager : IDisposable
{
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientManager> _logger;

    public McpClientManager(ILoggerFactory loggerFactory, ILogger<McpClientManager> logger)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<McpClient> StartClientAsync(McpServerConfig config, IReadOnlyDictionary<string, string>? envOverlay = null, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(config.Id, out var existing))
            return existing;

        var env = MergeEnv(config.Env, envOverlay);
        var log = _loggerFactory.CreateLogger($"MCP.{config.Id}");
        try
        {
            var client = await McpClient.ConnectAsync(
                config.Id,
                config.Command,
                config.Args ?? Array.Empty<string>(),
                env,
                _loggerFactory,
                log,
                ct).ConfigureAwait(false);
            _clients[config.Id] = client;
            _logger.LogInformation("MCP Client started: {Name} ({Command})", config.Name, config.Command);
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MCP Client {Name}", config.Name);
            throw;
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var kv in _clients)
        {
            try
            {
                await kv.Value.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP Client dispose: {Id}", kv.Key);
            }
            _logger.LogInformation("MCP Client stopped: {Id}", kv.Key);
        }
        _clients.Clear();
    }

    public void Dispose()
    {
        // IDisposable 桥接到异步 StopAll：宿主关闭时同步等待可接受。
#pragma warning disable VSTHRD002 // Avoid synchronous waits in IDisposable.Dispose
        StopAllAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    private static IReadOnlyDictionary<string, string>? MergeEnv(Dictionary<string, string>? configEnv, IReadOnlyDictionary<string, string>? overlay)
    {
        if (configEnv == null && overlay == null) return null;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (configEnv != null)
        {
            foreach (var kv in configEnv)
                merged[kv.Key] = kv.Value ?? "";
        }
        if (overlay != null)
        {
            foreach (var kv in overlay)
                merged[kv.Key] = kv.Value ?? "";
        }
        return merged.Count > 0 ? merged : null;
    }
}
