using System.Net;
using OpenWorkmate.Server.Security;
using Xunit;

namespace backend.Tests.Unit;

public class TestEndpointSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1/v1", true)]
    [InlineData("https://localhost/foo", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", false)]
    public void GetBlockedReason_RespectsAllowPrivate(string url, bool expectBlockedWhenDisallowed)
    {
        var uri = new Uri(url);
        var reason = TestEndpointSecurity.GetBlockedReason(uri, allowPrivateEndpoints: false);
        if (expectBlockedWhenDisallowed)
            Assert.False(string.IsNullOrEmpty(reason));
        else
            Assert.Null(reason);
    }

    [Fact]
    public void GetBlockedReason_AllowPrivate_PermitsLocalhost()
    {
        var uri = new Uri("http://127.0.0.1:8080/v1");
        Assert.Null(TestEndpointSecurity.GetBlockedReason(uri, allowPrivateEndpoints: true));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("172.20.0.1")]
    [InlineData("169.254.169.254")]
    public void GetBlockedReasonForIp_PrivateRanges(string ip)
    {
        Assert.NotNull(TestEndpointSecurity.GetBlockedReasonForIp(IPAddress.Parse(ip)));
    }
}
