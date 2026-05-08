using System.Text.Json;
using OpenWorkmate.Server.Services.ToolInvocation;
using Xunit;

namespace OpenWorkmate.Server.Tests.Unit;

public sealed class ToolScalarArgumentParserTests
{
    [Fact]
    public void TryReadBool_Undefined_False()
    {
        var el = default(JsonElement);
        Assert.False(ToolScalarArgumentParser.TryReadBool(el, out _));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("\"0\"", false)]
    [InlineData("\"yes\"", true)]
    [InlineData("\"no\"", false)]
    public void TryReadBool_JsonTokens(string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.True(ToolScalarArgumentParser.TryReadBool(doc.RootElement, out var v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void TryReadBool_NumberOneZero_Works()
    {
        using var doc = JsonDocument.Parse("1");
        Assert.True(ToolScalarArgumentParser.TryReadBool(doc.RootElement, out var v) && v);
        using var doc0 = JsonDocument.Parse("0");
        Assert.True(ToolScalarArgumentParser.TryReadBool(doc0.RootElement, out v) && !v);
    }

    [Fact]
    public void TryReadBool_Invalid_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse(""" "maybe" """);
        Assert.False(ToolScalarArgumentParser.TryReadBool(doc.RootElement, out _));
        using var arr = JsonDocument.Parse("[]");
        Assert.False(ToolScalarArgumentParser.TryReadBool(arr.RootElement, out _));
    }

    [Fact]
    public void TryReadBoolWithDefault_UndefinedOrNull_UsesDefault()
    {
        var u = default(JsonElement);
        Assert.True(ToolScalarArgumentParser.TryReadBoolWithDefault(u, true, out var v) && v);
        using var doc = JsonDocument.Parse("null");
        Assert.True(ToolScalarArgumentParser.TryReadBoolWithDefault(doc.RootElement, false, out v) && !v);
    }

    [Fact]
    public void TryReadBoolWithDefault_NullableOmitted_UsesDefault()
    {
        JsonElement? omitted = null;
        Assert.True(ToolScalarArgumentParser.TryReadBoolWithDefault(omitted, true, out var v) && v);
    }

    [Fact]
    public void IsOmitted_NullOrUndefined_True()
    {
        JsonElement? n = null;
        Assert.True(ToolScalarArgumentParser.IsOmitted(n));
        var u = default(JsonElement);
        Assert.True(ToolScalarArgumentParser.IsOmitted(u));
        using var doc = JsonDocument.Parse("null");
        Assert.True(ToolScalarArgumentParser.IsOmitted(doc.RootElement));
    }

    [Fact]
    public void TryReadNullableBool_Undefined_NotSpecified()
    {
        var u = default(JsonElement);
        Assert.True(ToolScalarArgumentParser.TryReadNullableBool(u, out var val, out var spec));
        Assert.Null(val);
        Assert.False(spec);
    }

    [Fact]
    public void TryReadNullableBool_Null_SpecifiedNull()
    {
        using var doc = JsonDocument.Parse("null");
        Assert.True(ToolScalarArgumentParser.TryReadNullableBool(doc.RootElement, out var val, out var spec));
        Assert.Null(val);
        Assert.True(spec);
    }

    [Fact]
    public void TryReadInt32_StringAndNumber()
    {
        using var doc = JsonDocument.Parse("42");
        Assert.True(ToolScalarArgumentParser.TryReadInt32(doc.RootElement, out var v) && v == 42);
        using var s = JsonDocument.Parse(""" "-7" """);
        Assert.True(ToolScalarArgumentParser.TryReadInt32(s.RootElement, out v) && v == -7);
    }

    [Fact]
    public void TryReadInt64_String_Works()
    {
        using var doc = JsonDocument.Parse(""" "9223372036854775807" """);
        Assert.True(ToolScalarArgumentParser.TryReadInt64(doc.RootElement, out var v));
        Assert.Equal(9223372036854775807L, v);
    }

    [Fact]
    public void TryReadDouble_StringInvariant()
    {
        using var doc = JsonDocument.Parse(""" "3.5" """);
        Assert.True(ToolScalarArgumentParser.TryReadDouble(doc.RootElement, out var v));
        Assert.Equal(3.5, v, 5);
    }

    [Fact]
    public void TryReadNullableInt32_Undefined_NotSpecified()
    {
        var u = default(JsonElement);
        Assert.True(ToolScalarArgumentParser.TryReadNullableInt32(u, out var val, out var spec));
        Assert.Null(val);
        Assert.False(spec);
    }

    [Fact]
    public void TryReadInt32WithDefault_Undefined_UsesDefault()
    {
        var u = default(JsonElement);
        Assert.True(ToolScalarArgumentParser.TryReadInt32WithDefault(u, 5, out var v) && v == 5);
        using var doc = JsonDocument.Parse("9");
        Assert.True(ToolScalarArgumentParser.TryReadInt32WithDefault(doc.RootElement, 5, out v) && v == 9);
    }
}
