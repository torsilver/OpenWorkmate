using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Mcp;

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

    public async Task<KernelPlugin> BuildPluginAsync(CancellationToken ct = default)
    {
        var tools = await _client.ListToolsAsync(ct);
        var functions = new List<KernelFunction>();

        foreach (var tool in tools)
        {
            var function = KernelFunctionFactory.CreateFromMethod(
                async (KernelArguments args) => await InvokeMcpToolAsync(tool.Name, args, ct),
                functionName: SanitizeFunctionName(tool.Name),
                description: tool.Description
            );
            
            functions.Add(function);
        }

        return KernelPluginFactory.CreateFromFunctions(_pluginName, functions);
    }

    private async Task<string> InvokeMcpToolAsync(string toolName, KernelArguments args, CancellationToken ct)
    {
        try
        {
            var mcpArgs = new Dictionary<string, object>();
            foreach (var arg in args)
            {
                if (arg.Value != null)
                {
                    mcpArgs[arg.Key] = arg.Value;
                }
            }

            var result = await _client.CallToolAsync(toolName, mcpArgs, ct);
            if (result.IsError)
            {
                var errorMsg = string.Join("\n", result.Content.Select(c => c.Text));
                _logger.LogWarning("[{Plugin}] MCP tool {Tool} returned error: {Message}", _pluginName, toolName, errorMsg);
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
