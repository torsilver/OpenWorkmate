using OpenWorkmate.Server.Services.Chat;
using Xunit;

namespace OpenWorkmate.Server.Tests.Unit;

public sealed class StreamingToolCallDeltaHelperTests
{
    [Fact]
    public void MaxArgumentsCumulativeCharsPerCall_Is32K()
    {
        Assert.Equal(32 * 1024, StreamingToolCallDeltaHelper.MaxArgumentsCumulativeCharsPerCall);
    }
}
