using System.Text.Json;
using OfficeCopilot.Server;
using Xunit;

namespace backend.Tests.Unit;

public class WsMessageUiThemeTests
{
    [Fact]
    public void UiThemeChanged_serializes_camelCase_uiThemeId()
    {
        var msg = new WsMessage { Type = "ui_theme_changed", UiThemeId = "minimal" };
        var json = JsonSerializer.Serialize(msg, JsonCtx.Default.WsMessage);
        Assert.Contains("\"type\":\"ui_theme_changed\"", json);
        Assert.Contains("\"uiThemeId\":\"minimal\"", json);
    }
}
