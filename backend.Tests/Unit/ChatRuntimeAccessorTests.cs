using Microsoft.Extensions.DependencyInjection;
using OfficeCopilot.Server.Services;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class ChatRuntimeAccessorTests
{
    private static ChatRuntimeAccessor CreateAccessor()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return new ChatRuntimeAccessor(sp);
    }

    [Fact]
    public void GetAllowedTools_when_no_tools_returns_empty()
    {
        var acc = CreateAccessor();
        Assert.Empty(acc.GetAllowedTools("chrome", "s1"));
    }

    [Fact]
    public void GetChatClient_when_no_clients_returns_null()
    {
        var acc = CreateAccessor();
        Assert.Null(acc.GetChatClient());
    }

    [Fact]
    public void IsReady_false_when_no_clients_set()
    {
        var acc = CreateAccessor();
        Assert.False(acc.IsReady);
    }

    [Fact]
    public void GetPluginServices_returns_app_services()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var acc = new ChatRuntimeAccessor(sp);
        Assert.Same(sp, acc.GetPluginServices());
    }
}
