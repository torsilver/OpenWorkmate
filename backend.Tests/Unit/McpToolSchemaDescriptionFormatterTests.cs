using System.Text.Json;
using OfficeCopilot.Server.Mcp;
using Xunit;

namespace backend.Tests.Unit;

public class McpToolSchemaDescriptionFormatterTests
{
    [Fact]
    public void CombineDescriptionWithInputSchema_AppendsCompactSchema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");
        var combined = McpToolSchemaDescriptionFormatter.CombineDescriptionWithInputSchema("Read a file.", doc.RootElement);
        Assert.Contains("Read a file.", combined, StringComparison.Ordinal);
        Assert.Contains("【MCP inputSchema】", combined, StringComparison.Ordinal);
        Assert.Contains("\"path\"", combined, StringComparison.Ordinal);
        Assert.Contains("\"required\"", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void CombineDescriptionWithInputSchema_SkipsEmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        var combined = McpToolSchemaDescriptionFormatter.CombineDescriptionWithInputSchema("Only text.", doc.RootElement);
        Assert.Equal("Only text.", combined);
    }
}
