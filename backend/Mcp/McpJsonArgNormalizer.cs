using System.Linq;
using System.Text.Json;

namespace OfficeCopilot.Server.Mcp;

/// <summary>MCP 工具调用参数：模型侧 JSON → <see cref="McpClient.CallToolAsync"/> 所需字典。</summary>
public static class McpJsonArgNormalizer
{
    public static Dictionary<string, object> JsonElementToObjectDict(JsonElement el)
    {
        var mcpArgs = new Dictionary<string, object>();
        if (el.ValueKind != JsonValueKind.Object)
            return mcpArgs;
        foreach (var p in el.EnumerateObject())
            mcpArgs[p.Name] = JsonElementToObject(p.Value)!;
        return mcpArgs;
    }

    public static object? JsonElementToObject(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)!),
            _ => el.GetRawText(),
        };
}
