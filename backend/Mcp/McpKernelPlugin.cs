using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OfficeCopilot.Server.Logging;

namespace OfficeCopilot.Server.Mcp;

/// <summary>
/// 将 MCP 服务器工具暴露为 MEAI <see cref="AITool"/> 列表。
/// </summary>
public sealed class McpKernelPlugin
{
    private readonly McpClient _client;
    private readonly string _pluginName;
    private readonly ILogger<McpKernelPlugin> _logger;

    public McpKernelPlugin(McpClient client, string pluginName, ILogger<McpKernelPlugin> logger)
    {
        _client = client;
        _pluginName = pluginName;
        _logger = logger;
    }

    /// <summary>构建 MEAI 工具列表。</summary>
    public async Task<IReadOnlyList<AITool>> BuildMcpAIToolsAsync(CancellationToken ct = default)
    {
        var tools = await _client.ListToolsAsync(ct);
        var list = new List<AITool>();
        foreach (var tool in tools)
        {
            var fnName = SanitizeFunctionName(tool.Name);
            var mcpName = tool.Name;
            var fn = AIFunctionFactory.Create(
                async (JsonElement arguments, CancellationToken cancellationToken) =>
                    await InvokeMcpToolFromJsonAsync(mcpName, arguments, cancellationToken).ConfigureAwait(false),
                new AIFunctionFactoryOptions
                {
                    Name = fnName,
                    Description = tool.Description ?? "",
                });
            list.Add(fn);
        }

        return list;
    }

    private async Task<string> InvokeMcpToolFromJsonAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        var mcpArgs = McpJsonArgNormalizer.JsonElementToObjectDict(arguments);
        return await InvokeMcpCoreAsync(toolName, mcpArgs, ct).ConfigureAwait(false);
    }

    private async Task<string> InvokeMcpCoreAsync(string toolName, Dictionary<string, object> mcpArgs, CancellationToken ct)
    {
        try
        {
            var result = await _client.CallToolAsync(toolName, mcpArgs, ct).ConfigureAwait(false);
            if (result.IsError)
            {
                var errorMsg = string.Join("\n", result.Content.Select(c => c.Text));
                _logger.LogWarning("[{Plugin}] MCP tool {Tool} returned error len={Len} preview={Message}",
                    _pluginName, toolName, errorMsg.Length, LogPreview.HeadTail(errorMsg, 160, 160));
                return $"[MCP 工具错误] {errorMsg}";
            }

            return string.Join("\n", result.Content.Select(c => c.Text));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Plugin}] MCP tool {Tool} threw", _pluginName, toolName);
            return $"[MCP 调用异常] {ex.Message}";
        }
    }

    private static string SanitizeFunctionName(string name)
    {
        return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }
}
