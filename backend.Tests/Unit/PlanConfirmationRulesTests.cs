using OfficeCopilot.Server.Services.Plan;
using Xunit;

namespace backend.Tests.Unit;

public class PlanConfirmationRulesTests
{
    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(3, 3, false)]
    [InlineData(4, 3, true)]
    [InlineData(10, 3, true)]
    public void RequiresUserConfirmation_StepVersusThreshold(int stepCount, int maxSteps, bool expected)
    {
        Assert.Equal(expected, PlanConfirmationRules.RequiresUserConfirmation(stepCount, maxSteps));
    }

    [Fact]
    public void RequiresUserConfirmation_InvalidMaxSteps_ClampedTo3()
    {
        Assert.False(PlanConfirmationRules.RequiresUserConfirmation(2, 0));
        Assert.True(PlanConfirmationRules.RequiresUserConfirmation(4, 0));
    }
}
