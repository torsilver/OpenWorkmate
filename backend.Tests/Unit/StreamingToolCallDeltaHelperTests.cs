using OfficeCopilot.Server.Services.Chat;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

public sealed class StreamingToolCallDeltaHelperTests
{
    [Fact]
    public void MaxArgumentsCumulativeCharsPerCall_Is32K()
    {
        Assert.Equal(32 * 1024, StreamingToolCallDeltaHelper.MaxArgumentsCumulativeCharsPerCall);
    }
}
