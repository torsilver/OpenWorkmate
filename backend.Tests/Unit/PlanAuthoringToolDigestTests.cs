using OpenWorkmate.Server.Services.Plan;
using Xunit;

namespace backend.Tests.Unit;

public class PlanAuthoringToolDigestTests
{
    [Fact]
    public void FormatOneLineDescriptionWithRiskHint_AppendsForRunCommand()
    {
        var line = PlanAuthoringToolDigest.FormatOneLineDescriptionWithRiskHint("CLI", "run_command", "Run shell.");
        Assert.Contains("Run shell.", line, StringComparison.Ordinal);
        Assert.Contains("需确认", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOneLineDescriptionWithRiskHint_NoHintForReadOnlyTool()
    {
        var line = PlanAuthoringToolDigest.FormatOneLineDescriptionWithRiskHint("System", "get_current_time", "返回时间。");
        Assert.Equal("返回时间。", line);
    }
}
