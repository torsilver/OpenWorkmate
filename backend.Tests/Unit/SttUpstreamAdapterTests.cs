using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public class SttUpstreamAdapterTests
{
    [Fact]
    public void BuildMinimalWavPcm16kMono_HasRiffAndMinimumSize()
    {
        var wav = SttUpstreamAdapter.BuildMinimalWavPcm16kMono(50);
        Assert.True(wav.Length > 44);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
    }
}
