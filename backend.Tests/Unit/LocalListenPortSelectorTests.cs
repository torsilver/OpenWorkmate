using System.Net;
using OfficeCopilot.Server.Security;
using Xunit;

namespace backend.Tests.Unit;

public class LocalListenPortSelectorTests
{
    [Fact]
    public void TryFindFirstAvailablePort_InvalidRange_ReturnsNegative1()
    {
        Assert.Equal(-1, LocalListenPortSelector.TryFindFirstAvailablePort(IPAddress.Loopback, 70000, 1));
        Assert.Equal(-1, LocalListenPortSelector.TryFindFirstAvailablePort(IPAddress.Loopback, 8765, 0));
    }
}
