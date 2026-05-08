using System.Text.Json;
using OpenWorkmate.Server.Plugins;
using Xunit;

namespace OpenWorkmate.Server.Tests.Unit;

public class BrowserPluginRpcTextTests
{
    [Fact]
    public void TryParseResultString_null_returns_null()
    {
        Assert.Null(BrowserPluginRpcText.TryParseResultString(null));
    }

    [Fact]
    public void TryParseResultString_string_returns_value()
    {
        var el = JsonDocument.Parse("\"hello\"").RootElement;
        Assert.Equal("hello", BrowserPluginRpcText.TryParseResultString(el));
    }

    [Fact]
    public void TryParseResultString_empty_string_preserved()
    {
        var el = JsonDocument.Parse("\"\"").RootElement;
        Assert.Equal("", BrowserPluginRpcText.TryParseResultString(el));
    }

    [Fact]
    public void TryParseResultString_null_json_returns_null()
    {
        var el = JsonDocument.Parse("null").RootElement;
        Assert.Null(BrowserPluginRpcText.TryParseResultString(el));
    }

    [Fact]
    public void IsEffectivelyEmpty_whitespace_true()
    {
        Assert.True(BrowserPluginRpcText.IsEffectivelyEmpty("   "));
        Assert.True(BrowserPluginRpcText.IsEffectivelyEmpty(""));
        Assert.True(BrowserPluginRpcText.IsEffectivelyEmpty(null));
    }

    [Fact]
    public void PageScriptEmptyNotice_contains_kind()
    {
        var msg = BrowserPluginRpcText.PageScriptEmptyNotice(JsonValueKind.String);
        Assert.Contains("tail", msg);
        Assert.Contains("String", msg);
    }
}
