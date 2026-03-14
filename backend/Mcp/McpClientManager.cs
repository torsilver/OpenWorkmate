using System.Collections.Concurrent;

namespace OfficeCopilot.Server.Mcp;

public sealed class McpClientManager : IDisposable
{
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ILogger<McpClientManager> _logger;

    public McpClientManager(ILogger<McpClientManager> logger)
    {
        _logger = logger;
    }

    public async Task<McpClient> StartClientAsync(McpServerConfig config, IReadOnlyDictionary<string, string>? envOverlay = null, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(config.Id, out var existing))
        {
            return existing; // 已经运行中
        }

        var env = MergeEnv(config.Env, envOverlay);
        var client = new McpClient(config.Id, config.Command, config.Args ?? Array.Empty<string>(), env, _logger);
        try
        {
            await client.StartAsync(ct);
            _clients[config.Id] = client;
            _logger.LogInformation("MCP Client started: {Name} ({Command})", config.Name, config.Command);
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MCP Client {Name}", config.Name);
            client.Dispose();
            throw;
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var kv in _clients)
        {
            kv.Value.Dispose();
            _logger.LogInformation("MCP Client stopped: {Id}", kv.Key);
        }
        _clients.Clear();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _ = StopAllAsync();
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
