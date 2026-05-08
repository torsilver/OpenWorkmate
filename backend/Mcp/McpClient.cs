using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace OpenWorkmate.Server.Mcp;

/// <summary>
/// MCP 客户端：基于官方 <see href="https://www.nuget.org/packages/ModelContextProtocol">ModelContextProtocol</see> SDK（stdio）。
/// </summary>
public sealed class McpClient : IAsyncDisposable, IDisposable
{
    private readonly string _id;
    private readonly ILogger _logger;
    private readonly SdkMcpClient _sdk;

    public string Id => _id;

    private McpClient(string id, ILogger logger, SdkMcpClient sdk)
    {
        _id = id;
        _logger = logger;
        _sdk = sdk;
    }

    public static async Task<McpClient> ConnectAsync(
        string id,
        string command,
        string[] args,
        IReadOnlyDictionary<string, string>? env,
        ILoggerFactory loggerFactory,
        ILogger logger,
        CancellationToken ct = default)
    {
        var opts = new StdioClientTransportOptions
        {
            Name = id,
            Command = command,
            Arguments = args ?? Array.Empty<string>(),
        };
        if (env != null)
        {
            opts.EnvironmentVariables ??= new Dictionary<string, string?>();
            foreach (var kv in env)
                opts.EnvironmentVariables[kv.Key] = kv.Value ?? "";
        }

        var transport = new StdioClientTransport(opts, loggerFactory);
        var sdk = await SdkMcpClient.CreateAsync(transport, new McpClientOptions(), loggerFactory, ct).ConfigureAwait(false);
        return new McpClient(id, logger, sdk);
    }

    public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var tools = await _sdk.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        var list = new List<McpTool>();
        foreach (var t in tools)
        {
            var tool = new McpTool
            {
                Name = t.Name ?? "",
                Description = t.Description ?? "",
            };
            try
            {
                if (t.JsonSchema is JsonElement je && je.ValueKind != JsonValueKind.Undefined)
                    tool.InputSchema = je;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MCP {Id}] Tool {Name} input schema serialization skipped.", _id, tool.Name);
            }
            list.Add(tool);
        }
        return list;
    }

    public async Task<McpCallToolResult> CallToolAsync(string name, Dictionary<string, object> args, CancellationToken ct = default)
    {
        try
        {
            IReadOnlyDictionary<string, object?> argDict = args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var result = await _sdk.CallToolAsync(name, argDict, cancellationToken: ct).ConfigureAwait(false);
            var mapped = new McpCallToolResult { IsError = result.IsError == true };
            if (result.Content is { Count: > 0 })
            {
                foreach (var block in result.Content)
                {
                    if (block is TextContentBlock tb)
                        mapped.Content.Add(new McpContent { Type = "text", Text = tb.Text ?? "" });
                    else
                        mapped.Content.Add(new McpContent { Type = "text", Text = block.ToString() ?? "" });
                }
            }
            return mapped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MCP {Id}] CallToolAsync failed for tool {Tool}", _id, name);
            return new McpCallToolResult
            {
                IsError = true,
                Content = new List<McpContent> { new() { Type = "text", Text = ex.Message } }
            };
        }
    }

    public void Dispose()
    {
        // IDisposable 桥接到 IAsyncDisposable：宿主关闭时同步等待异步释放为常见做法。
#pragma warning disable VSTHRD002 // Avoid synchronous waits in IDisposable.Dispose
        DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Sdk 会话释放时会结束子进程并释放 stdio 传输层，勿再单独 Dispose transport。
            await _sdk.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MCP {Id}] Sdk dispose", _id);
        }
    }
}
