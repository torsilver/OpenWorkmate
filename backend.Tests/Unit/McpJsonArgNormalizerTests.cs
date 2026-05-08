using System.Text.Json;
using OpenWorkmate.Server.Mcp;
using Xunit;

namespace OpenWorkmate.Server.Tests.Unit;

public sealed class McpJsonArgNormalizerTests
{
    [Fact]
    public void JsonElementToObjectDict_EmptyObject_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        var d = McpJsonArgNormalizer.JsonElementToObjectDict(doc.RootElement);
        Assert.Empty(d);
    }

    [Fact]
    public void JsonElementToObjectDict_FlatValues_Normalizes()
    {
        using var doc = JsonDocument.Parse("""{"a":"x","b":1,"c":true,"d":null}""");
        var d = McpJsonArgNormalizer.JsonElementToObjectDict(doc.RootElement);
        Assert.Equal("x", d["a"]);
        Assert.Equal(1, Convert.ToInt64(d["b"]));
        Assert.Equal(true, d["c"]);
        Assert.Null(d["d"]);
    }

    [Fact]
    public void JsonElementToObject_NestedObject_Works()
    {
        using var doc = JsonDocument.Parse("""{"n":{"k":2}}""");
        var d = McpJsonArgNormalizer.JsonElementToObjectDict(doc.RootElement);
        Assert.IsType<Dictionary<string, object>>(d["n"]);
        var inner = (Dictionary<string, object>)d["n"];
        Assert.Equal(2, Convert.ToInt64(inner["k"]));
    }
}
