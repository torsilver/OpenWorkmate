using Microsoft.SemanticKernel;
using System.Text.Json;

namespace OfficeCopilot.Server.Mcp;

public sealed class McpKernelPlugin
{
    private readonly McpClient _client;
    private readonly string _pluginName;

    public McpKernelPlugin(McpClient client, string pluginName)
    {
        _client = client;
        _pluginName = pluginName;
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
                return $"[MCP Error] {errorMsg}";
            }

            return string.Join("\n", result.Content.Select(c => c.Text));
        }
        catch (Exception ex)
        {
            return $"[MCP Client Exception] {ex.Message}";
        }
    }

    private static string SanitizeFunctionName(string name)
    {
        return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }
}
