using Microsoft.Extensions.DependencyInjection;
using OfficeCopilot.Server.Services.SemanticKernel;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

/// <summary>仅验证默认协调器可从与生产相同的 DI 形状解析（不启动 LocalRuntime）。</summary>
public sealed class DefaultChatTurnProcessCoordinatorTests
{
    [Fact]
    public void DefaultChatTurnProcessCoordinator_resolves_from_ServiceCollection_with_ChatService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ChatService>(_ => null!);
        services.AddSingleton<IChatTurnProcessCoordinator, DefaultChatTurnProcessCoordinator>();
        var sp = services.BuildServiceProvider();
        var coord = sp.GetRequiredService<IChatTurnProcessCoordinator>();
        Assert.IsType<DefaultChatTurnProcessCoordinator>(coord);
    }
}
